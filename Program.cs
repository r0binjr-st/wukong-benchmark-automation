using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic;
using Microsoft.Win32;

// Координаты окна должны учитывать масштабирование Windows.
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

// Путь можно передать аргументом. Иначе ищем игру в библиотеках Steam.
string? settingsPath = args.Length > 0
    ? Path.GetFullPath(args[0].Trim('"'))
    : FindSettingsPath();

if (settingsPath == null || !File.Exists(settingsPath))
{
    Console.WriteLine("GameUserSettings.ini not found.");
    Console.WriteLine("Launch the benchmark manually once to create its settings.");
    Console.WriteLine("Or pass the full INI path: dotnet run -- \"path to INI\"");
    return;
}

string projectDirectory = Directory.GetCurrentDirectory();
string[] profileNames = { "CPU", "GPU" };

foreach (string name in profileNames)
{
    if (!File.Exists(Path.Combine(projectDirectory, name.ToLowerInvariant() + ".ini")))
    {
        Console.WriteLine(name.ToLowerInvariant() + ".ini not found in the current directory.");
        return;
    }
}

if (IsBenchmarkRunning())
{
    Console.WriteLine("Close the benchmark before running this program.");
    return;
}

string backupPath = settingsPath + ".backup";
if (File.Exists(backupPath))
{
    Console.WriteLine("Backup already exists: " + backupPath);
    Console.WriteLine("Restore it before running again. Do not overwrite it.");
    return;
}

Screen monitor = Screen.PrimaryScreen
    ?? throw new InvalidOperationException("No primary display found.");

string historyDirectory = Path.Combine(
    Path.GetTempPath(), "b1", "BenchMarkHistory", "Tool");
Directory.CreateDirectory(historyDirectory);

string outputDirectory = Path.Combine(
    projectDirectory, "results", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
Directory.CreateDirectory(outputDirectory);

// CreateNew не позволит перезаписать резервную копию другого запуска.
byte[] originalBytes = File.ReadAllBytes(settingsPath);
using (FileStream backup = new(backupPath, FileMode.CreateNew, FileAccess.Write))
{
    backup.Write(originalBytes);
}

Console.WriteLine("Settings: " + settingsPath);
Console.WriteLine("Backup saved");

List<RunResult> results = new();
string? failure = null;
bool restored = false;

try
{
    foreach (string name in profileNames)
    {
        cancellation.Token.ThrowIfCancellationRequested();

        // Перед следующим профилем ждём завершения предыдущего запуска.
        if (results.Count > 0)
            WaitForBenchmarkToStop(cancellation.Token);

        string templatePath = Path.Combine(
            projectDirectory, name.ToLowerInvariant() + ".ini");

        // Адаптируем копию профиля. Сами cpu.ini и gpu.ini не меняем.
        PreparedProfile profile = PrepareProfile(
            File.ReadAllText(templatePath), name, monitor);

        string appliedPath = Path.Combine(
            outputDirectory, name.ToLowerInvariant() + "-applied.ini");
        File.WriteAllText(appliedPath, profile.Text);
        File.WriteAllText(settingsPath, profile.Text);

        Console.WriteLine();
        Console.WriteLine(name + " profile applied: "
            + profile.Width + " x " + profile.Height);

        string json = RunBenchmark(historyDirectory, cancellation.Token);

        // Сохраняем родной результат отдельно для каждого прогона.
        File.WriteAllText(Path.Combine(
            outputDirectory, name.ToLowerInvariant() + "-result.json"), json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        // Сравнение FPS должно проходить без генерации кадров.
        if (GetValue(root, "InsertFrame") != "0")
            throw new InvalidOperationException(name + ": frame generation is still enabled.");

        results.Add(new RunResult(
            name, json, profile.Width, profile.Height, appliedPath));

        Console.WriteLine(name + " average FPS: " + GetValue(root, "FPSAvg"));
    }
}
catch (OperationCanceledException)
{
    failure = "Cancelled by user.";
    Console.WriteLine(failure);
    Environment.ExitCode = 1;
}
catch (Exception error)
{
    failure = error.ToString();
    Console.WriteLine("Error: " + error.Message);
    Environment.ExitCode = 1;
}
finally
{
    // RunBenchmark закрывает процесс до возвращения сюда.
    // При ошибке восстановления оставляем резервную копию.
    try
    {
        File.WriteAllBytes(settingsPath, originalBytes);
        File.Delete(backupPath);
        restored = true;
        Console.WriteLine("Settings restored");
    }
    catch (Exception error)
    {
        failure = (failure == null ? "" : failure + Environment.NewLine)
            + "Settings restore failed: " + error.Message;
        Console.WriteLine("Settings restore failed. Backup kept: " + backupPath);
        Environment.ExitCode = 1;
    }
}

string reportPath = Path.Combine(outputDirectory, "report.txt");
WriteReport(reportPath, results, restored, failure);
Console.WriteLine("Report: " + reportPath);

// Для найденных процессов используем снимок потоков, без открытия handle.
// Завершение своего процесса по-прежнему подтверждаем через WaitForExit.
static bool IsBenchmarkRunning()
{
    Process[] processes = Process.GetProcessesByName("b1-Win64-Shipping");
    bool running = false;
    foreach (Process process in processes)
    {
        using (process)
        {
            if (IsProcessRunning(process))
                running = true;
        }
    }
    return running;
}

static bool IsProcessRunning(Process process)
{
    try
    {
        process.Refresh();
        return process.Threads.Count > 0;
    }
    catch (InvalidOperationException)
    {
        return false; // Процесс исчез между поиском и проверкой.
    }
}

static void WaitForBenchmarkToStop(CancellationToken token)
{
    Stopwatch wait = Stopwatch.StartNew();
    while (IsBenchmarkRunning())
    {
        if (wait.Elapsed.TotalSeconds >= 15)
            throw new TimeoutException("Previous benchmark is still running after 15 seconds.");
        Pause(500, token);
    }
}

// Поиск ограничен библиотеками Steam, весь диск не сканируется.
static string? FindSettingsPath()
{
    HashSet<string> libraries = new(StringComparer.OrdinalIgnoreCase);
    string? steamPath = Registry.GetValue(
        @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;

    if (string.IsNullOrWhiteSpace(steamPath))
        steamPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");

    libraries.Add(steamPath);

    string libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
    if (File.Exists(libraryFile))
    {
        foreach (Match match in Regex.Matches(
            File.ReadAllText(libraryFile), "\"path\"\\s*\"([^\"]+)\""))
        {
            libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }
    }

    foreach (string library in libraries)
    {
        string installDirectory = "Black Myth Wukong Benchmark Tool";
        string manifest = Path.Combine(library, "steamapps", "appmanifest_3132990.acf");
        if (File.Exists(manifest))
        {
            Match match = Regex.Match(
                File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
            if (match.Success)
                installDirectory = match.Groups[1].Value;
        }

        string path = Path.Combine(library, "steamapps", "common",
            installDirectory, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
        if (File.Exists(path))
            return path;
    }
    return null;
}

// Меняем размеры, связанные поля INI и параметры внутри UISettingData.
static PreparedProfile PrepareProfile(string template, string name, Screen monitor)
{
    bool gpu = name == "GPU";
    int width = monitor.Bounds.Width;
    int height = monitor.Bounds.Height;

    if (!gpu)
    {
        // Сохраняем пропорции проверенного CPU-окна 1920 x 1280.
        // На меньшем экране уменьшаем его, чтобы оно помещалось.
        double fit = Math.Min(1.0, Math.Min(
            Math.Max(1, monitor.WorkingArea.Width - 40) / 1920.0,
            Math.Max(1, monitor.WorkingArea.Height - 60) / 1280.0));
        width = Math.Max(2, (int)(1920 * fit) / 2 * 2);
        height = Math.Max(2, (int)(1280 * fit) / 2 * 2);
    }

    double scale = gpu ? 100 : 28.125;
    int renderWidth = (int)Math.Round(width * scale / 100);
    int renderHeight = (int)Math.Round(height * scale / 100);
    string mode = gpu ? "1" : "2";
    int x = gpu ? -1 : monitor.WorkingArea.Left + (monitor.WorkingArea.Width - width) / 2;
    int y = gpu ? -1 : monitor.WorkingArea.Top + (monitor.WorkingArea.Height - height) / 2;

    Dictionary<string, string> values = new()
    {
        ["ResolutionSizeX"] = width.ToString(),
        ["ResolutionSizeY"] = height.ToString(),
        ["LastUserConfirmedResolutionSizeX"] = width.ToString(),
        ["LastUserConfirmedResolutionSizeY"] = height.ToString(),
        ["DesiredScreenWidth"] = renderWidth.ToString(),
        ["DesiredScreenHeight"] = renderHeight.ToString(),
        ["LastUserConfirmedDesiredScreenWidth"] = renderWidth.ToString(),
        ["LastUserConfirmedDesiredScreenHeight"] = renderHeight.ToString(),
        ["FullscreenMode"] = mode,
        ["LastConfirmedFullscreenMode"] = mode,
        ["PreferredFullscreenMode"] = mode,
        ["WindowPosX"] = x.ToString(),
        ["WindowPosY"] = y.ToString(),
        ["bUseVSync"] = "False",
        ["bUseDynamicResolution"] = "False",
        ["FrameRateLimit"] = "0.000000",
        ["sg.ResolutionQuality"] = scale.ToString(CultureInfo.InvariantCulture),
        ["sg.RayTracingQuality"] = "0",
        ["r.RayTracing.EnableInGame"] = "False"
    };

    Dictionary<string, string> uiValues = new()
    {
        ["MainDisplay"] = "0",
        ["ScreenMode"] = mode,
        ["InsertFrame"] = "0",
        ["Rtx"] = "0",
        ["Vsync"] = "0",
        ["LockFrameRate"] = "0"
    };
    // Сохраняем проверенное значение CPU-профиля на исходном размере окна.
    if (gpu || width != 1920 || height != 1280)
        uiValues["ImageQuality"] = renderHeight.ToString();
    // В GPU-профиле выбираем основной пункт разрешения монитора.
    if (gpu)
        uiValues["ScreenResolution"] = "0";

    List<string> lines = new();
    foreach (string line in template.Replace("\r\n", "\n").Split('\n'))
    {
        // Не переносим идентификатор монитора с чужого компьютера.
        if (line.StartsWith("MainMonitorID="))
            continue;

        int separator = line.IndexOf('=');
        string key = separator > 0 ? line[..separator] : "";

        if (values.TryGetValue(key, out string? value))
            lines.Add(key + "=" + value);
        else if (key == "UISettingData")
        {
            lines.Add(Regex.Replace(line, "\"([^\"]+)\"\\s*,\\s*\"([^\"]*)\"",
                match => uiValues.TryGetValue(match.Groups[1].Value, out string? replacement)
                    ? "\"" + match.Groups[1].Value + "\", \"" + replacement + "\""
                    : match.Value));
        }
        else
            lines.Add(line);
    }
    return new PreparedProfile(string.Join(Environment.NewLine, lines), width, height);
}

// Выполняем один прогон. Закрытие процесса происходит даже при отмене или ошибке.
static string RunBenchmark(string historyDirectory, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    if (IsBenchmarkRunning())
        throw new InvalidOperationException("Benchmark is already running.");

    HashSet<string> oldFiles = new(
        Directory.GetFiles(historyDirectory), StringComparer.OrdinalIgnoreCase);

    ProcessStartInfo launch = new("steam://run/3132990");
    launch.UseShellExecute = true;
    using Process? steamProcess = Process.Start(launch);

    Process? benchmark = null;
    Stopwatch startup = Stopwatch.StartNew();
    while (benchmark == null)
    {
        // После отправки команды Steam сначала получаем процесс.
        // Даже при Ctrl+C его нужно закрыть до восстановления INI.
        Process[] found = Process.GetProcessesByName("b1-Win64-Shipping");
        foreach (Process process in found)
        {
            try
            {
                if (benchmark == null && IsProcessRunning(process))
                    benchmark = process;
            }
            catch (InvalidOperationException)
            {
                // Процесс исчез между поиском и проверкой.
            }
            finally
            {
                if (!ReferenceEquals(benchmark, process))
                    process.Dispose();
            }
        }

        if (benchmark == null)
        {
            if (startup.Elapsed.TotalSeconds >= 120)
                throw new TimeoutException("Benchmark process did not appear.");
            Thread.Sleep(500);
        }
    }

    try
    {
        IntPtr retainedHandle = benchmark.Handle;
        Console.WriteLine("Benchmark PID: " + benchmark.Id);
        Console.WriteLine("Waiting for startup...");
        // Пока оставляем проверенные 40 секунд загрузки.
        Pause(40000, token);

        ActivateBenchmark(benchmark);
        Pause(500, token);
        SendKeys.SendWait("{ENTER}");
        Pause(3000, token);

        ActivateBenchmark(benchmark);
        benchmark.Refresh();
        Cursor.Position = BenchmarkWindow.GetMenuPoint(
            benchmark.MainWindowHandle, 0.12, 0.46);
        Pause(500, token);
        SendKeys.SendWait("{ENTER}");
        Pause(3000, token);

        ActivateBenchmark(benchmark);
        Pause(500, token);
        SendKeys.SendWait("{ENTER}");
        Console.WriteLine("Start commands sent. Waiting for a new result...");

        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed.TotalMinutes < 10)
        {
            token.ThrowIfCancellationRequested();
            foreach (string file in Directory.GetFiles(historyDirectory))
            {
                if (oldFiles.Contains(file))
                    continue;

                try
                {
                    string json = File.ReadAllText(file);
                    using JsonDocument check = JsonDocument.Parse(json);
                    JsonElement root = check.RootElement;
                    root.GetProperty("FPSAvg").GetDouble();
                    root.GetProperty("FPSMin").GetDouble();
                    root.GetProperty("FPSMax").GetDouble();
                    root.GetProperty("ScreenResolution").GetString();
                    Console.WriteLine("New result saved: " + file);
                    return json;
                }
                catch (IOException) { }
                catch (JsonException) { }
            }

            if (benchmark.HasExited)
                throw new InvalidOperationException(
                    "Benchmark exited without a result: " + benchmark.ExitCode);
            Pause(1000, token);
        }
        throw new TimeoutException("No result after 10 minutes.");
    }
    finally
    {
        try
        {
            if (!benchmark.HasExited)
            {
                Console.WriteLine("Closing benchmark...");
                benchmark.CloseMainWindow();
                if (!benchmark.WaitForExit(10000))
                {
                    Console.WriteLine("Forcing benchmark to stop...");
                    benchmark.Kill();
                    benchmark.WaitForExit();
                }
            }
        }
        finally
        {
            benchmark.Dispose();
        }
    }
}

static void ActivateBenchmark(Process benchmark)
{
    if (benchmark.HasExited)
        throw new InvalidOperationException("Benchmark exited: " + benchmark.ExitCode);
    Interaction.AppActivate(benchmark.Id);
}

// Пауза прерывается по Ctrl+C, затем выполняются finally.
static void Pause(int milliseconds, CancellationToken token)
{
    if (token.WaitHandle.WaitOne(milliseconds))
        token.ThrowIfCancellationRequested();
}

static string GetValue(JsonElement root, string key)
{
    return root.TryGetProperty(key, out JsonElement value) ? value.ToString() : "not reported";
}

// Аппаратные данные и фактические настройки берём из родных результатов.
static void WriteReport(string path, List<RunResult> results, bool restored, string? failure)
{
    StringBuilder report = new();
    report.AppendLine("Black Myth Wukong benchmark automation");
    report.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    report.AppendLine("Status: " + (results.Count == 2 && restored && failure == null
        ? "Completed" : "Incomplete"));
    report.AppendLine("Original settings restored: " + restored);
    if (failure != null)
        report.AppendLine("Error: " + failure);

    if (results.Count > 0)
    {
        using JsonDocument hardware = JsonDocument.Parse(results[0].Json);
        report.AppendLine();
        report.AppendLine("CPU: " + GetValue(hardware.RootElement, "CPUModel"));
        report.AppendLine("GPU: " + GetValue(hardware.RootElement, "GPUModel"));
        report.AppendLine("RAM: " + GetValue(hardware.RootElement, "SysMem"));
        report.AppendLine("GPU driver: " + GetValue(hardware.RootElement, "GpuDriverVer"));
    }

    foreach (RunResult run in results)
    {
        using JsonDocument document = JsonDocument.Parse(run.Json);
        JsonElement root = document.RootElement;
        report.AppendLine();
        report.AppendLine(run.Name + " profile");
        report.AppendLine("FPS average / minimum / maximum: "
            + GetValue(root, "FPSAvg") + " / " + GetValue(root, "FPSMin")
            + " / " + GetValue(root, "FPSMax"));
        report.AppendLine("FPS, 5th percentile: " + GetValue(root, "FPS95"));
        report.AppendLine("GPU usage reported by benchmark, %: " + GetValue(root, "GPUAvg"));
        report.AppendLine("Requested resolution: " + run.Width + " x " + run.Height);
        report.AppendLine("Actual resolution: " + GetValue(root, "ScreenResolution"));

        string[] keys = { "ScreenMode", "QualityLevel", "ImageQuality",
            "ViewDistance", "AntiAliasing", "PostProcessing", "ShadowQuality",
            "TextureQuality", "MaterialQuality", "VegetationQuality",
            "Dlss", "InsertFrame", "Rtx", "Dx12", "MotionBlur" };
        foreach (string key in keys)
            report.AppendLine(key + ": " + GetValue(root, key));

        report.AppendLine("VSync and FPS limiter: disabled in applied INI");
        report.AppendLine("Applied INI: " + run.AppliedPath);

        // Игра может иначе применить разрешение из шаблона.
        string actual = Regex.Replace(GetValue(root, "ScreenResolution"), @"\s+", "");
        if (actual.Replace('×', 'x') != run.Width + "x" + run.Height)
            report.AppendLine("Note: actual resolution differs from requested resolution.");
    }

    report.AppendLine();
    report.AppendLine("CPU profile reduces GPU load; it does not guarantee a CPU bottleneck.");
    report.AppendLine("Full ray tracing is disabled in both profiles.");
    report.AppendLine("Native graphics option numbers are preserved from benchmark results.");
    File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
}

record PreparedProfile(string Text, int Width, int Height);
record RunResult(string Name, string Json, int Width, int Height, string AppliedPath);

// Работа с областью игры без рамки и заголовка окна.
static class BenchmarkWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref WindowPoint point);

    public static Point GetMenuPoint(IntPtr window, double relativeX, double relativeY)
    {
        if (window == IntPtr.Zero || !GetClientRect(window, out WindowRect rect))
            throw new InvalidOperationException("Cannot get benchmark window size.");

        WindowPoint point = new();
        point.X = (int)((rect.Right - rect.Left) * relativeX);
        point.Y = (int)((rect.Bottom - rect.Top) * relativeY);
        if (!ClientToScreen(window, ref point))
            throw new InvalidOperationException("Cannot get benchmark window position.");
        return new Point(point.X, point.Y);
    }
}

