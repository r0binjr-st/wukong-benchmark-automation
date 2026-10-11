using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

// OCR работает локально. Изображения и текст не отправляются в интернет.
sealed class MenuAutomation : IDisposable
{
    private readonly TesseractEngine engine;
    private Bitmap? lastImage;
    private Rectangle lastWindow;
    private string lastText = "";

    public MenuAutomation(string dataDirectory, string logPath)
    {
        foreach (string language in new[] { "rus", "eng" })
            if (!File.Exists(Path.Combine(dataDirectory, language + ".traineddata")))
                throw new FileNotFoundException("OCR model missing: " + language + ".traineddata");
        try
        {
            engine = new TesseractEngine(dataDirectory, "rus+eng", EngineMode.LstmOnly);
            engine.SetVariable("debug_file", Path.GetFullPath(logPath));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "Cannot initialize OCR. Check tessdata and install Microsoft Visual C++ "
                + "Redistributable x64 (see README).", error);
        }
    }

    public void StartBenchmark(Process process, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        lastImage?.Dispose();
        lastImage = null;
        lastText = "";
        try
        {
            Console.WriteLine("Waiting for the startup screen or menu (RU / EN)...");
            MenuMatch screen = WaitFor(process, directory, "startup", 120, token,
                MenuScreen.Splash, MenuScreen.MainMenu, MenuScreen.Confirmation);
            if (screen.Screen == MenuScreen.Splash)
            {
                Focus(process, token);
                SendKeys.SendWait("{ENTER}");
                Console.WriteLine("Startup prompt found. Enter sent.");
                screen = WaitFor(process, directory, "main-menu", 90, token,
                    MenuScreen.MainMenu, MenuScreen.Confirmation);
            }
            if (screen.Screen == MenuScreen.MainMenu)
            {
                Click(process, screen.Target, token);
                Console.WriteLine("Benchmark menu item found and clicked.");
                screen = WaitFor(process, directory, "confirmation", 60, token,
                    MenuScreen.Confirmation);
            }
            Click(process, screen.Target, token);
            Console.WriteLine("Confirmation found and clicked.");
            WaitForScene(process, directory, screen.Target, token);
        }
        catch (Exception error)
        {
            // Сохраняем последний снимок именно игрового окна, а не всего рабочего стола.
            try
            {
                lastImage?.Save(Path.Combine(directory, "last-screen.png"), ImageFormat.Png);
                File.WriteAllText(Path.Combine(directory, "last-ocr.txt"), lastText);
                File.WriteAllText(Path.Combine(directory, "error.txt"), error.ToString());
            }
            catch (Exception saveError)
            {
                Console.WriteLine("Cannot save menu diagnostics: " + saveError.Message);
            }
            Console.WriteLine("Menu diagnostics: " + directory);
            throw;
        }
    }

    private MenuMatch WaitFor(Process process, string directory, string stage,
        int seconds, CancellationToken token, params MenuScreen[] accepted)
    {
        Stopwatch timer = Stopwatch.StartNew();
        MenuMatch previous = new(MenuScreen.Unknown, Rectangle.Empty);
        Rectangle previousWindow = Rectangle.Empty;
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            token.ThrowIfCancellationRequested();
            MenuMatch current = ReadScreen(process, token, accepted);
            if (accepted.Contains(current.Screen) && current.Screen == previous.Screen
                && lastWindow == previousWindow
                && Math.Abs(current.Target.X - previous.Target.X) <= 20
                && Math.Abs(current.Target.Y - previous.Target.Y) <= 20)
            {
                lastImage!.Save(Path.Combine(directory, stage + ".png"), ImageFormat.Png);
                File.WriteAllText(Path.Combine(directory, stage + "-ocr.txt"), lastText);
                Console.WriteLine(stage + " detected in "
                    + timer.Elapsed.TotalSeconds.ToString("F1") + " s.");
                return current;
            }
            previous = current;
            previousWindow = lastWindow;
            Pause(250, token);
        }
        throw new TimeoutException("Menu screen not found: " + stage + " (" + seconds + " s).");
    }

    private void WaitForScene(Process process, string directory, Rectangle confirmation,
        CancellationToken token)
    {
        Stopwatch timer = Stopwatch.StartNew();
        int absent = 0;
        int retries = 0;
        TimeSpan lastClick = TimeSpan.Zero;
        while (timer.Elapsed.TotalSeconds < 45)
        {
            Pause(250, token);
            // Кнопка уже найдена: читаем только её область, а не всю сцену игры.
            MenuMatch screen = ReadConfirmation(process, confirmation, token);
            bool closed = MenuText.ConfirmationHasClosed(screen.Screen, ref absent);
            if (screen.Screen == MenuScreen.Confirmation)
            {
                if (timer.Elapsed - lastClick >= TimeSpan.FromSeconds(5) && retries < 2)
                {
                    Click(process, screen.Target, token);
                    lastClick = timer.Elapsed;
                    retries++;
                }
            }
            else if (closed)
            {
                // Один полный снимок отличает переход к сцене от возврата в главное меню.
                MenuMatch check = ReadScreen(process, token,
                    MenuScreen.MainMenu, MenuScreen.Confirmation);
                if (check.Screen == MenuScreen.MainMenu)
                    throw new InvalidOperationException("Returned to the menu instead of starting the test.");
                if (check.Screen == MenuScreen.Confirmation)
                {
                    absent = 0;
                    confirmation = check.Target;
                    continue;
                }
                // Исчезновение диалога означает переход. Успех прогона подтвердит новый JSON.
                lastImage!.Save(Path.Combine(directory, "after-confirmation.png"), ImageFormat.Png);
                Console.WriteLine("Confirmation closed. Waiting for benchmark results.");
                return;
            }
        }
        throw new TimeoutException("Confirmation did not close after 45 seconds.");
    }

    private bool Capture(Process process, CancellationToken token)
    {
        IntPtr window = Focus(process, token);
        if (window == IntPtr.Zero)
            return false;
        Rectangle bounds = WindowInput.GetClientBounds(window);
        if (bounds.Width < 100 || bounds.Height < 100)
            return false;

        using Bitmap capture = new(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(capture))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        lastImage?.Dispose();
        lastImage = (Bitmap)capture.Clone();
        lastWindow = bounds;
        return true;
    }

    private MenuMatch ReadScreen(Process process, CancellationToken token,
        params MenuScreen[] accepted)
    {
        if (!Capture(process, token) || IsDarkFrame(lastImage!))
        {
            lastText = "Dark or unavailable frame; OCR skipped.";
            return new(MenuScreen.Unknown, Rectangle.Empty);
        }
        Bitmap capture = lastImage!;

        // Одинаковый размер текста для OCR на маленьких и больших окнах.
        double scale = 1920.0 / capture.Width;
        using Bitmap resized = new(1920, Math.Max(1, (int)Math.Round(capture.Height * scale)));
        using (Graphics graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(capture, 0, 0, resized.Width, resized.Height);
        }
        using MemoryStream stream = new();
        resized.Save(stream, ImageFormat.Png);
        using Pix pixels = Pix.LoadFromMemory(stream.ToArray());
        List<string> readings = new();
        // Выделенные кнопки лучше читаются в Auto / SingleBlock, обычные — в SparseText.
        PageSegMode[] modes = accepted.Length == 1 && accepted[0] == MenuScreen.Confirmation
            ? new[] { PageSegMode.Auto, PageSegMode.SingleBlock }
            : new[] { PageSegMode.SparseText, PageSegMode.Auto, PageSegMode.SingleBlock };
        foreach (PageSegMode mode in modes)
        {
            token.ThrowIfCancellationRequested();
            using Page page = engine.Process(pixels, mode);
            readings.Add(mode + ":\n" + page.GetText());
            List<OcrWord> words = new();
            using ResultIterator iterator = page.GetIterator();
            iterator.Begin();
            do
            {
                string? text = iterator.GetText(PageIteratorLevel.Word);
                if (!string.IsNullOrWhiteSpace(text)
                    && iterator.TryGetBoundingBox(PageIteratorLevel.Word, out Tesseract.Rect rect))
                    words.Add(new(text, new Rectangle(rect.X1, rect.Y1, rect.Width, rect.Height)));
            }
            while (iterator.Next(PageIteratorLevel.Word));
            MenuMatch match = MenuText.Detect(words);
            lastText = string.Join("\n\n", readings);
            if (accepted.Contains(match.Screen))
            {
                Rectangle target = match.Target;
                return new(match.Screen, new Rectangle(
                    (int)Math.Round(target.X / scale), (int)Math.Round(target.Y / scale),
                    Math.Max(1, (int)Math.Round(target.Width / scale)),
                    Math.Max(1, (int)Math.Round(target.Height / scale))));
            }
        }
        return new(MenuScreen.Unknown, Rectangle.Empty);
    }

    private MenuMatch ReadConfirmation(Process process, Rectangle target, CancellationToken token)
    {
        if (!Capture(process, token) || IsDarkFrame(lastImage!))
        {
            lastText = "Dark or unavailable frame; OCR skipped.";
            return new(MenuScreen.Unknown, Rectangle.Empty);
        }
        Rectangle region = target;
        region.Inflate(Math.Max(12, target.Height), Math.Max(6, target.Height / 2));
        region.Intersect(new Rectangle(Point.Empty, lastImage!.Size));
        if (region.Width <= 0 || region.Height <= 0)
            return new(MenuScreen.Unknown, Rectangle.Empty);

        // Масштабируем лишь строку с кнопкой до высоты букв примерно 32 px.
        double scale = Math.Clamp(32.0 / Math.Max(1, target.Height), 1, 4);
        using Bitmap cropped = lastImage.Clone(region, PixelFormat.Format24bppRgb);
        using Bitmap resized = new((int)Math.Round(cropped.Width * scale),
            (int)Math.Round(cropped.Height * scale));
        using (Graphics graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(cropped, 0, 0, resized.Width, resized.Height);
        }
        using MemoryStream stream = new();
        resized.Save(stream, ImageFormat.Png);
        using Pix pixels = Pix.LoadFromMemory(stream.ToArray());
        using Page page = engine.Process(pixels, PageSegMode.SingleLine);
        lastText = "Confirmation region: " + region + "\n" + page.GetText();
        List<OcrWord> words = new();
        using ResultIterator iterator = page.GetIterator();
        iterator.Begin();
        do
        {
            string? text = iterator.GetText(PageIteratorLevel.Word);
            if (!string.IsNullOrWhiteSpace(text)
                && iterator.TryGetBoundingBox(PageIteratorLevel.Word, out Tesseract.Rect rect))
                words.Add(new(text, new Rectangle(rect.X1, rect.Y1, rect.Width, rect.Height)));
        }
        while (iterator.Next(PageIteratorLevel.Word));
        Rectangle? found = MenuText.FindConfirmationLabel(words);
        return found != null
            ? new(MenuScreen.Confirmation, target)
            : new(MenuScreen.Unknown, Rectangle.Empty);
    }

    private static bool IsDarkFrame(Bitmap image)
    {
        // Быстрая проверка чёрного экрана загрузки до вызова нейросети OCR.
        int bright = 0;
        int stepX = Math.Max(1, image.Width / 64);
        int stepY = Math.Max(1, image.Height / 36);
        for (int y = stepY / 2; y < image.Height; y += stepY)
            for (int x = stepX / 2; x < image.Width; x += stepX)
            {
                Color pixel = image.GetPixel(x, y);
                if (pixel.R + pixel.G + pixel.B > 45 && ++bright >= 3)
                    return false;
            }
        return true;
    }

    private void Click(Process process, Rectangle target, CancellationToken token)
    {
        IntPtr window = Focus(process, token);
        if (window == IntPtr.Zero || WindowInput.GetClientBounds(window) != lastWindow)
            throw new InvalidOperationException("Benchmark window moved before the click. Run again.");
        Cursor.Position = new Point(lastWindow.Left + target.Left + target.Width / 2,
            lastWindow.Top + target.Top + target.Height / 2);
        Pause(150, token); // Даём интерфейсу выделить найденную кнопку.
        WindowInput.CheckForeground(window);
        WindowInput.LeftButton(true);
        try { Pause(80, token); }
        finally { WindowInput.LeftButton(false); }
    }

    private static IntPtr Focus(Process process, CancellationToken token)
    {
        if (process.HasExited)
            throw new InvalidOperationException("Benchmark exited: " + process.ExitCode);
        process.Refresh();
        IntPtr window = process.MainWindowHandle;
        if (window != IntPtr.Zero)
        {
            WindowInput.RestoreIfMinimized(window);
            if (!WindowInput.IsForeground(window))
            {
                Interaction.AppActivate(process.Id);
                for (int i = 0; i < 20 && !WindowInput.IsForeground(window); i++)
                    Pause(50, token);
            }
            WindowInput.CheckForeground(window);
        }
        return window;
    }

    private static void Pause(int milliseconds, CancellationToken token)
    {
        if (token.WaitHandle.WaitOne(milliseconds))
            token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        lastImage?.Dispose();
        engine.Dispose();
    }
}

static class WindowInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    public static Rectangle GetClientBounds(IntPtr window)
    {
        NativePoint origin = new();
        if (!GetClientRect(window, out NativeRect rect) || !ClientToScreen(window, ref origin))
            throw new InvalidOperationException("Cannot get benchmark window bounds.");
        return new(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static void RestoreIfMinimized(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, 9);
    }

    public static void CheckForeground(IntPtr window)
    {
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("Cannot focus benchmark. Keep its window in front.");
    }

    public static bool IsForeground(IntPtr window) => GetForegroundWindow() == window;

    public static void LeftButton(bool down)
    {
        Input input = new() { Type = 0, Data = new InputUnion {
            Mouse = new MouseInput { Flags = down ? 0x0002u : 0x0004u } } };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot send a mouse click.");
    }
}
