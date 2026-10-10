using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WukongBenchmark;

sealed record RunResult(Preset Preset, Metrics? Metrics, string? Error, string? Warning, string? Screenshot);

static class Program
{
    const string SteamAppId = "3132990"; 
    const string GameProcess = "b1-Win64-Shipping"; 
    const string LauncherProcess = "b1_benchmark";
    const string InstallFolderName = "Black Myth Wukong Benchmark Tool";

    static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5); 
    static readonly TimeSpan BenchmarkTimeout = TimeSpan.FromMinutes(8);
    const int ManualPromptAfterSeconds = 90; 
    const int BenchmarkMinSeconds = 100; 

    static readonly string[] StartButtonWords = { "быстродейств", "performance", "запустить", "run benchmark" };

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Native.SetProcessDPIAware(); 

        Console.WriteLine("=== Black Myth: Wukong Benchmark Tool — автоматический тест CPU и GPU ===\n");

        string? install = FindInstallDir(args);
        if (install == null)
        {
            Error("Не найдена папка Benchmark Tool. Укажите путь первым аргументом, например:\n" +
                  "  WukongBenchmark.exe \"D:\\SteamLibrary\\steamapps\\common\\Black Myth Wukong Benchmark Tool\"");
            return 1;
        }

        string iniPath = Path.Combine(install, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
        if (!File.Exists(iniPath))
        {
            Error($"Не найден {iniPath}\nЗапустите бенчмарк вручную один раз и закройте его — файл появится.");
            return 1;
        }

        string outDir = Path.Combine(AppContext.BaseDirectory, "results", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);

        Console.WriteLine("[1/3] Сбор характеристик компьютера...");
        var specs = SystemInfo.Collect();
        Console.WriteLine(specs.ToText());

        string backup = iniPath + ".orig";
        if (File.Exists(backup)) File.Copy(backup, iniPath, true);
        else File.Copy(iniPath, backup);

        var presets = new[] { Presets.Cpu(specs.Display), Presets.Gpu(specs.Display) };
        var results = new List<RunResult>();
        try
        {
            for (int i = 0; i < presets.Length; i++)
            {
                Console.WriteLine($"\n[{i + 2}/3] {presets[i].Title}");
                results.Add(await RunPreset(presets[i], iniPath, outDir));
            }
        }
        finally
        {
            KillGame();
            File.Copy(backup, iniPath, true);
            File.Delete(backup);
            Console.WriteLine("\nИсходные настройки бенчмарка восстановлены.");
        }

        string report = BuildReport(specs, results);
        File.WriteAllText(Path.Combine(outDir, "report.md"), report, Encoding.UTF8);

        Console.WriteLine("\n==================== ИТОГОВЫЙ ОТЧЁТ ====================\n");
        Console.WriteLine(report);
        Console.WriteLine($"Отчёт и скриншоты сохранены в: {outDir}");
        Console.WriteLine("Нажмите Enter для выхода...");
        Console.ReadLine();
        return 0;
    }

    static async Task<RunResult> RunPreset(Preset p, string iniPath, string outDir)
    {
        try
        {
            KillGame();
            await Task.Delay(3000);

            File.Copy(iniPath + ".orig", iniPath, true);
            IniEditor.Apply(iniPath, p);
            Console.WriteLine("  Настройки применены. Запуск через Steam...");
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });

            Process game = await WaitForGameWindow();
            Console.WriteLine("  Окно бенчмарка появилось. Ищу кнопку запуска...");

            var total = Stopwatch.StartNew();
            Stopwatch? run = null; 
            Bitmap? final = null;
            string? warning = null;
            int logoClicks = 0;     
            var lastLogoClick = Stopwatch.StartNew();
            bool menuClicked = false;    
            var lastMenuClick = Stopwatch.StartNew();

            while (final == null)
            {
                game.Refresh();
                if (game.HasExited)
                    throw new InvalidOperationException("Процесс бенчмарка завершился во время теста (возможно, крэш).");
                if (run != null && run.Elapsed > BenchmarkTimeout)
                    throw new TimeoutException("Результаты не появились вовремя. Возможно, кнопка старта не была нажата.");

                if (run != null && run.Elapsed.TotalSeconds < BenchmarkMinSeconds)
                {
                    await Task.Delay(1000);
                    continue;
                }

                if (game.MainWindowHandle == IntPtr.Zero || !Native.GetWindowRect(game.MainWindowHandle, out var rect))
                {
                    await Task.Delay(1000);
                    continue;
                }

                using var shot = ScreenTools.Capture(rect);
                var ocr = await ScreenTools.RecognizeAsync(shot);

                if (IsResultsScreen(ocr.Text))
                {
                    await Task.Delay(2000);   
                    final = ScreenTools.Capture(rect);
                    break;
                }

                if (run == null)
                {
                    var confirm = FindText(ocr, new[] { "подтвердить", "confirm" });
                    var btn = confirm == null && lastMenuClick.Elapsed.TotalSeconds > 6 ? FindText(ocr, StartButtonWords) : null;

                    if (confirm != null)
                    {
                        Native.SetForegroundWindow(game.MainWindowHandle);
                        await Task.Delay(500);
                        Native.Click(rect.Left + confirm.Value.X, rect.Top + confirm.Value.Y);
                        run = Stopwatch.StartNew();
                        Console.WriteLine($"  Запуск подтверждён. Идёт замер (~{BenchmarkMinSeconds} с без вмешательства)...");
                    }
                    else if (btn != null)
                    {
                        Native.SetForegroundWindow(game.MainWindowHandle);
                        await Task.Delay(500);
                        Native.Click(rect.Left + btn.Value.X, rect.Top + btn.Value.Y);
                        menuClicked = true;
                        lastMenuClick.Restart();
                        Console.WriteLine("  Нажат «Тест быстродействия», жду окно подтверждения...");
                    }
                    else if (!menuClicked && logoClicks < 4 && lastLogoClick.Elapsed.TotalSeconds > 4)
                    {
                        Native.SetForegroundWindow(game.MainWindowHandle);
                        await Task.Delay(300);
                        Native.Click(rect.Left + (rect.Right - rect.Left) / 2, rect.Top + (rect.Bottom - rect.Top) / 2);
                        logoClicks++;
                        lastLogoClick.Restart();
                        Console.WriteLine($"  Заставка: клик для перехода в меню ({logoClicks}/4)...");
                    }
                    else if (total.Elapsed.TotalSeconds > ManualPromptAfterSeconds)
                    {
                        Console.WriteLine("  Автоматически пройти меню не удалось.\n" +
                                          "  >>> В окне игры выберите «Тест быстродействия» и нажмите «Подтвердить», затем вернитесь сюда и нажмите Enter.");
                        await Task.Run(() => Console.ReadLine());
                        run = Stopwatch.StartNew();
                        warning = "Запуск теста выполнен вручную (кнопка не распознана).";
                    }
                }

                await Task.Delay(run == null ? 2000 : 4000);
            }

            string shotPath = Path.Combine(outDir, $"result_{p.Id}.png");
            final.Save(shotPath, ImageFormat.Png);

            Metrics m = await ResultParser.ParseAsync(final, Path.Combine(outDir, "debug_" + p.Id));
            final.Dispose();

            if (m.Resolution != null && !m.Resolution.Equals(p.ExpectedResolution, StringComparison.OrdinalIgnoreCase))
                warning = (warning == null ? "" : warning + " ") +
                          $"Тул отработал на {m.Resolution}, ожидалось {p.ExpectedResolution} — проверьте индекс ScreenResolution в Presets.cs.";
            if (!m.Ok)
                warning = (warning == null ? "" : warning + " ") + "Числа FPS распознаны не полностью — сверьтесь со скриншотом.";

            Console.WriteLine($"  Готово: avg {F(m.Avg)}, max {F(m.Max)}, min {F(m.Min)}, 5% {F(m.P5)} FPS; VRAM {m.VramGb ?? "н/д"} ГБ");
            if (warning != null) Console.WriteLine("  Внимание: " + warning);

            KillGame();
            return new RunResult(p, m, null, warning, shotPath);
        }
        catch (Exception ex)
        {
            Error($"Проход «{p.Id}» не завершён: {ex.Message}");
            KillGame();
            return new RunResult(p, null, ex.Message, null, null);
        }
    }

    static async Task<Process> WaitForGameWindow()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StartupTimeout)
        {
            var proc = Process.GetProcessesByName(GameProcess).FirstOrDefault();
            if (proc != null)
            {
                proc.Refresh();
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    await Task.Delay(5000); 
                    proc.Refresh();
                    return proc;
                }
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Процесс {GameProcess}.exe не появился. Проверьте, что Steam запущен и AppID ({SteamAppId}) верный.");
    }

    static bool IsResultsScreen(string text) =>
        text.Contains("fps", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(text, @"результат\w*\s+тест|test\s+result|benchmark\s+result", RegexOptions.IgnoreCase);

    static Point? FindText(Windows.Media.Ocr.OcrResult ocr, IEnumerable<string> words)
    {
        foreach (string word in words)
        {
            foreach (var line in ocr.Lines)
            {
                if (line.Words.Count == 0 || line.Text.Contains('?')) continue;
                if (!line.Text.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
                double x1 = line.Words.Min(w => w.BoundingRect.X);
                double x2 = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                double y1 = line.Words.Min(w => w.BoundingRect.Y);
                double y2 = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                return new Point((int)((x1 + x2) / 2), (int)((y1 + y2) / 2));
            }
        }
        return null;
    }

    static void KillGame()
    {
        foreach (string name in new[] { GameProcess, LauncherProcess })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(true); p.WaitForExit(10000); } catch { /* уже закрыт */ }
            }
    }

    static string? FindInstallDir(string[] args)
    {
        if (args.Length > 0 && Directory.Exists(args[0])) return args[0];

        var libraries = new List<string>();
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
        {
            libraries.Add(steam);
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        libraries.Add(@"C:\Program Files (x86)\Steam");

        foreach (string lib in libraries)
        {
            string dir = Path.Combine(lib, "steamapps", "common", InstallFolderName);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    static string F(int? v) => v?.ToString() ?? "н/д";

    static string BuildReport(SystemSpecs s, List<RunResult> rs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Black Myth: Wukong Benchmark Tool — отчёт");
        sb.AppendLine($"\nДата: {DateTime.Now:yyyy-MM-dd HH:mm}\n");

        sb.AppendLine("## Характеристики компьютера\n");
        sb.AppendLine("| Параметр | Значение |\n|---|---|");
        sb.AppendLine($"| CPU | {s.Cpu} |");
        sb.AppendLine($"| GPU | {s.Gpu} |");
        sb.AppendLine($"| RAM | {s.Ram} |");
        sb.AppendLine($"| ОС | {s.Os} |");
        sb.AppendLine($"| Экран | {s.Display} |");

        sb.AppendLine("\n## Результаты\n");
        sb.AppendLine("| Тест | Среднее FPS | Макс. FPS | Мин. FPS | 5-й перцентиль | Видеопамять, ГБ | Статус |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rs)
        {
            string name = r.Preset.Id == "cpu" ? "CPU-тест" : "GPU-тест";
            if (r.Metrics is { } m)
                sb.AppendLine($"| {name} | {F(m.Avg)} | {F(m.Max)} | {F(m.Min)} | {F(m.P5)} | {m.VramGb ?? "н/д"} | {(r.Warning ?? "OK")} |");
            else
                sb.AppendLine($"| {name} | — | — | — | — | — | Ошибка: {r.Error} |");
        }

        foreach (var r in rs)
        {
            sb.AppendLine($"\n## Настройки: {r.Preset.Title}\n");
            sb.AppendLine("| Параметр | Значение |\n|---|---|");
            foreach (var (n, v) in r.Preset.Human) sb.AppendLine($"| {n} | {v} |");
            sb.AppendLine($"\n**Почему такие настройки.** {r.Preset.Rationale}\n");
            if (r.Metrics != null)
            {
                sb.AppendLine($"Что тул показал на экране результатов (OCR): {r.Metrics.SettingsRaw}\n");
                if (!r.Metrics.Ok)
                    sb.AppendLine($"Отладка, левый блок с FPS (OCR): {r.Metrics.LeftRaw}\n");
            }
            if (r.Screenshot != null)
                sb.AppendLine($"Скриншот: `{Path.GetFileName(r.Screenshot)}`");
        }
        return sb.ToString();
    }

    static void Error(string msg)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(msg);
        Console.ForegroundColor = old;
    }
}
