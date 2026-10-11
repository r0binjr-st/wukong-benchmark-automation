using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Отчёт читает готовые результаты. Настройки игры здесь не меняются.
static class BenchmarkReport
{
    private record Row(string Label, string Cpu, string Gpu);

    public static void Write(string directory, IReadOnlyList<RunResult> runs,
        bool restored, string? failure, DateTime? date = null, string? note = null)
    {
        // Clone позволяет закрыть JsonDocument, сохранив прочитанные данные.
        var data = new Dictionary<string, JsonElement>();
        var profiles = new Dictionary<string, RunResult>();
        foreach (RunResult run in runs)
        {
            using JsonDocument document = JsonDocument.Parse(run.Json);
            data[run.Name] = document.RootElement.Clone();
            profiles[run.Name] = run;
        }

        string Value(string name, string key)
        {
            if (!data.TryGetValue(name, out JsonElement root)
                || !root.TryGetProperty(key, out JsonElement value)
                || value.ValueKind == JsonValueKind.Null)
                return "—";
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
                ? number.ToString("0.##", CultureInfo.InvariantCulture) : value.ToString();
        }
        string Requested(string name) => profiles.TryGetValue(name, out RunResult? run)
            ? $"{run.Width} × {run.Height}" : "—";
        string Hardware(string key) => data.ContainsKey("CPU")
            ? Value("CPU", key) : Value("GPU", key);

        List<Row> summary = new();
        void Metric(string label, string key) => summary.Add(new(label, Value("CPU", key), Value("GPU", key)));
        Metric("Средний FPS", "FPSAvg");
        Metric("Минимальный FPS", "FPSMin");
        Metric("Максимальный FPS", "FPSMax");
        Metric("FPS, 5-й перцентиль", "FPS95");
        Metric("Загрузка GPU, %", "GPUAvg");
        summary.Add(new("Запрошенное разрешение", Requested("CPU"), Requested("GPU")));
        Metric("Фактическое разрешение", "ScreenResolution");
        summary.Add(new("Качество графики", Quality(Value("CPU", "QualityLevel")), Quality(Value("GPU", "QualityLevel"))));

        var settings = new List<Row>();
        (string Label, string Key)[] options =
        {
            ("Режим экрана", "ScreenMode"), ("Масштаб рендера (значение игры)", "ImageQuality"),
            ("Дальность прорисовки", "ViewDistance"), ("Сглаживание", "AntiAliasing"),
            ("Постобработка", "PostProcessing"), ("Тени", "ShadowQuality"),
            ("Текстуры", "TextureQuality"), ("Материалы", "MaterialQuality"),
            ("Растительность", "VegetationQuality"), ("Апскейлер (Dlss)", "Dlss"),
            ("Генерация кадров", "InsertFrame"), ("Полная трассировка лучей", "Rtx"),
            ("DirectX 12", "Dx12"), ("Размытие в движении", "MotionBlur")
        };
        foreach (var (label, key) in options)
            settings.Add(new(label, Setting(key, Value("CPU", key)), Setting(key, Value("GPU", key))));

        bool complete = data.ContainsKey("CPU") && data.ContainsKey("GPU") && restored && failure == null;
        string status = complete ? "Завершён" : "Не завершён";
        string timestamp = (date ?? DateTime.Now).ToString("dd.MM.yyyy HH:mm:ss");
        string restoreStatus = restored ? "Исходные настройки восстановлены" : "Исходные настройки НЕ восстановлены";
        List<string> notices = new();
        if (!restored)
            notices.Add("Закрой бенчмарк и восстанови GameUserSettings.ini из файла .backup. Резервную копию пока не удаляй.");
        foreach (string name in new[] { "CPU", "GPU" })
        {
            if (!data.ContainsKey(name))
                notices.Add(name + ": результат не получен.");
            else if (Value(name, "ScreenResolution") != "—"
                && NormalizeResolution(Value(name, "ScreenResolution")) != NormalizeResolution(Requested(name)))
                notices.Add(name + ": фактическое разрешение отличается от запрошенного.");
        }
        if (failure != null)
            notices.Add("Ошибка: " + failure.Split('\n')[0].TrimEnd('\r'));

        const string explanation = "CPU-профиль снижает нагрузку на видеокарту, но не гарантирует упор в процессор. FPS двух профилей получен при разных настройках.";
        StringBuilder html = new();
        html.Append("""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Wukong — результаты бенчмарка</title>
            <style>
            *{box-sizing:border-box}body{margin:0;background:#f3f5f8;color:#202b3d;font:16px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
            main{max-width:1000px;margin:auto;padding:36px 24px}h1{font-size:30px;line-height:1.2;margin:4px 0 10px}h2{font-size:20px;margin:0 0 18px}
            .muted{color:#5b6575}.top{margin-bottom:24px}.badges{display:flex;flex-wrap:wrap;gap:10px;margin-top:16px}.badge{border-radius:6px;padding:6px 12px;font-weight:600}
            .ok{background:#e1f1e7;color:#205a37}.warn{background:#fff0da;color:#79521c}.panel{background:white;border:1px solid #dde3ec;border-radius:12px;padding:24px;margin:18px 0}
            .scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;text-align:left}th,td{padding:12px 14px;border-bottom:1px solid #e7ebf1;vertical-align:top}
            thead{background:#f3f6fa}th{font-weight:600}th:first-child{width:46%}td{font-variant-numeric:tabular-nums}tbody tr:first-child td{font-size:25px;font-weight:700;color:#245baa}
            .settings tbody tr:first-child td{font:inherit;color:inherit}.hardware{display:grid;grid-template-columns:100px 1fr;gap:10px 18px;margin:0}.hardware dt{color:#5b6575}.hardware dd{margin:0;overflow-wrap:anywhere}
            summary{cursor:pointer;font-size:20px;font-weight:600}details[open] summary{margin-bottom:18px}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:13px}
            a{color:#245baa}p{margin:12px 0}.files{display:flex;flex-wrap:wrap;gap:12px 24px}.small{font-size:14px}.notice{border-left:4px solid #cf9236;padding-left:14px}
            @media(max-width:600px){main{padding:20px 12px}h1{font-size:24px}.panel{padding:16px}th,td{padding:10px 8px;font-size:14px}.hardware{grid-template-columns:70px 1fr}.badge{font-size:13px}}
            @media print{body{background:white}.panel{break-inside:avoid}details> :not(summary){display:block!important}}
            </style></head><body><main><header class="top"><div class="muted">Black Myth: Wukong Benchmark Tool</div><h1>Результаты прогонов</h1>
            """);
        html.Append($"<div class=\"muted\">{H(timestamp)}</div><div class=\"badges\"><span class=\"badge {(complete ? "ok" : "warn")}\">{H(status)}</span><span class=\"badge {(restored ? "ok" : "warn")}\">{H(restoreStatus)}</span></div></header>");
        if (note != null) html.Append($"<p class=\"notice\">{H(note)}</p>");
        foreach (string notice in notices) html.Append($"<p class=\"notice\">{H(notice)}</p>");
        html.Append("<section class=\"panel\"><h2>Два профиля</h2>");
        Table(html, summary);
        html.Append("<p class=\"small muted\">5-й перцентиль — нижняя граница для 95% измеренных значений FPS.</p>");
        html.Append("<p class=\"small muted\">CPU — низкая графика и сниженный масштаб рендера. GPU — высокая графика и масштаб 100%.</p>");
        html.Append($"<p class=\"small muted\">{H(explanation)}</p></section>");
        html.Append("<section class=\"panel\"><h2>Компьютер</h2><dl class=\"hardware\">");
        foreach (var (label, key) in new[] { ("CPU", "CPUModel"), ("GPU", "GPUModel"), ("RAM", "SysMem"), ("Драйвер", "GpuDriverVer") })
            html.Append($"<dt>{H(label)}</dt><dd>{H(Hardware(key))}</dd>");
        html.Append("</dl></section><details class=\"panel settings\"><summary>Подробные настройки</summary>");
        Table(html, settings);
        html.Append("<p class=\"small muted\">Настройки взяты из результата игры. Неизвестные коды сохранены без расшифровки. VSync и ограничение FPS выключены в применённых INI.</p></details>");
        html.Append("<section class=\"panel\"><h2>Исходные файлы</h2><div class=\"files\">");
        foreach (string name in new[] { "CPU", "GPU" })
        {
            if (!data.ContainsKey(name)) continue;
            string prefix = name.ToLowerInvariant();
            html.Append($"<span>{name}: <a href=\"{prefix}-result.json\">результат JSON</a> · <a href=\"{prefix}-applied.ini\">применённый INI</a></span>");
        }
        html.Append("<a href=\"report.txt\">Текстовый отчёт</a></div></section>");
        if (failure != null)
            html.Append($"<details class=\"panel\"><summary>Технические детали ошибки</summary><pre>{H(failure)}</pre></details>");
        html.Append("</main></body></html>");

        StringBuilder text = new();
        text.AppendLine("Black Myth: Wukong — результаты прогонов");
        text.AppendLine(timestamp + " | " + status);
        text.AppendLine(restoreStatus);
        if (note != null) text.AppendLine(note);
        foreach (string notice in notices) text.AppendLine(notice);
        text.AppendLine();
        TextTable(text, summary);
        text.AppendLine();
        foreach (var (label, key) in new[] { ("CPU", "CPUModel"), ("GPU", "GPUModel"), ("RAM", "SysMem"), ("Драйвер", "GpuDriverVer") })
            text.AppendLine(label + ": " + Hardware(key));
        text.AppendLine();
        text.AppendLine(explanation);
        text.AppendLine("Подробные настройки и детали ошибки: report.html");
        text.AppendLine("Исходные данные: cpu-result.json / gpu-result.json; применённые настройки: *-applied.ini.");
        // BOM позволяет старому Windows PowerShell правильно прочитать кириллицу.
        File.WriteAllText(Path.Combine(directory, "report.html"), html.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "report.txt"), text.ToString(), new UTF8Encoding(true));
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string NormalizeResolution(string value) => Regex.Replace(value, @"\s+", "").Replace('×', 'x').Replace('Х', 'x').Replace('х', 'x').ToLowerInvariant();
    private static string Quality(string value) => value switch
    {
        "1" => "Низкое (код 1)", "3" => "Высокое (код 3)", "—" => value, _ => "Код " + value
    };
    private static string Setting(string key, string value)
    {
        if (value == "—") return value;
        if (key is "InsertFrame" or "Rtx" or "Dx12")
            return value switch { "0" => "Выкл. (код 0)", "1" => "Вкл. (код 1)", _ => "Код " + value };
        if (key == "ScreenMode")
            return value switch { "1" => "Без рамки (код 1)", "2" => "Окно (код 2)", _ => "Код " + value };
        return key == "ImageQuality" ? value : "Код " + value;
    }
    private static void Table(StringBuilder html, IEnumerable<Row> rows)
    {
        html.Append("<div class=\"scroll\"><table><thead><tr><th scope=\"col\">Показатель</th><th scope=\"col\">CPU-профиль</th><th scope=\"col\">GPU-профиль</th></tr></thead><tbody>");
        foreach (Row row in rows)
            html.Append($"<tr><th scope=\"row\">{H(row.Label)}</th><td>{H(row.Cpu)}</td><td>{H(row.Gpu)}</td></tr>");
        html.Append("</tbody></table></div>");
    }
    private static void TextTable(StringBuilder text, IEnumerable<Row> rows)
    {
        text.AppendLine("Показатель".PadRight(30) + "CPU-профиль".PadRight(24) + "GPU-профиль");
        foreach (Row row in rows)
            text.AppendLine(row.Label.PadRight(30) + row.Cpu.PadRight(24) + row.Gpu);
    }
}

record RunResult(string Name, string Json, int Width, int Height, string AppliedPath);
