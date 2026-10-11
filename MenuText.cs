using System.Drawing;
using System.Text.RegularExpressions;

enum MenuScreen { Unknown, Splash, MainMenu, Confirmation }
record OcrWord(string Text, Rectangle Bounds);
record MenuMatch(MenuScreen Screen, Rectangle Target);

// Отдельно от захвата экрана: здесь только поиск фраз и их координат.
static class MenuText
{
    public static Rectangle? FindConfirmationLabel(IReadOnlyList<OcrWord> words) =>
        Find(words, "подтвердить", "confirm");

    public static bool ConfirmationHasClosed(MenuScreen screen, ref int absentFrames)
    {
        // Первый кадр без диалога должен сохраниться до следующей проверки.
        absentFrames = screen == MenuScreen.Unknown ? absentFrames + 1 : 0;
        return absentFrames >= 2;
    }

    public static MenuMatch Detect(IReadOnlyList<OcrWord> words)
    {
        Rectangle? confirm = Find(words, "подтвердить", "confirm");
        Rectangle? cancel = Find(words, "отменить", "cancel");
        // Проверяем обе кнопки, чтобы не принять слово в другом меню за диалог.
        if (confirm != null && cancel != null)
            return new(MenuScreen.Confirmation, confirm.Value);

        Rectangle? benchmark = Find(words, "тест быстродействия", "benchmark",
            "benchmark test", "performance test", "run benchmark", "start benchmark");
        Rectangle? settings = Find(words, "настройки", "settings");
        Rectangle? exit = Find(words, "выйти", "exit", "quit");
        if (benchmark != null && settings != null && exit != null)
            return new(MenuScreen.MainMenu, benchmark.Value);

        Rectangle? splash = Find(words, "нажмите любую кнопку", "нажмите любую клавишу",
            "press any button", "press any key", "press any button to continue",
            "press any key to continue");
        return splash != null
            ? new(MenuScreen.Splash, splash.Value)
            : new(MenuScreen.Unknown, Rectangle.Empty);
    }

    private static Rectangle? Find(IReadOnlyList<OcrWord> words, params string[] phrases)
    {
        HashSet<string> targets = phrases.Select(Normalize).ToHashSet();
        for (int i = 0; i < words.Count; i++)
        {
            string text = "";
            Rectangle bounds = words[i].Bounds;
            for (int j = i; j < Math.Min(words.Count, i + 7); j++)
            {
                // Фразу нельзя склеивать из далёких друг от друга участков экрана.
                if (j > i && !AreNeighbours(words[j - 1].Bounds, words[j].Bounds))
                    break;
                text += Normalize(words[j].Text);
                bounds = Rectangle.Union(bounds, words[j].Bounds);
                if (targets.Contains(text))
                    return bounds;
            }
        }
        return null;
    }

    private static bool AreNeighbours(Rectangle previous, Rectangle next)
    {
        int height = Math.Max(previous.Height, next.Height);
        bool sameLine = Math.Abs(previous.Top - next.Top) <= height / 2
            && next.Left >= previous.Left && next.Left - previous.Right <= height * 5;
        bool nextLine = next.Top >= previous.Bottom - height / 3
            && next.Top - previous.Bottom <= height
            && Math.Abs(next.Left - previous.Left) <= height * 12;
        return sameLine || nextLine;
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant().Replace('ё', 'е'), @"[^a-zа-я0-9]", "");
}
