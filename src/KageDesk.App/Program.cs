using KageDesk.App.Native;
using KageDesk.App.Services;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Export;
using KageDesk.Rendering.Snapshots;

namespace KageDesk.App;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\KageDesk_SingleInstance_Mutex";

    [STAThread]
    private static int Main(string[] args)
    {
        // 1. Process CLI Arguments
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "--benchmark", StringComparison.OrdinalIgnoreCase))
            {
                Win32Interop.AttachConsole(Win32Interop.ATTACH_PARENT_PROCESS);
                double seconds = 6.0;
                if (args.Length > 1 && double.TryParse(args[1], out double s))
                {
                    seconds = s;
                }

                string? outPath = null;
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase))
                    {
                        outPath = args[i + 1];
                        break;
                    }
                }

                return BenchmarkRunner.RunAsync(seconds, outPath).GetAwaiter().GetResult();
            }

            if (string.Equals(args[0], "--snapshot", StringComparison.OrdinalIgnoreCase))
            {
                Win32Interop.AttachConsole(Win32Interop.ATTACH_PARENT_PROCESS);
                string outDir = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "snapshots");

                string themesDir = FindThemesDirectory();
                var themes = ThemePackLoader.LoadThemes(themesDir);
                var written = PreviewSnapshotGenerator.GenerateAll(outDir, themes);

                Console.WriteLine($"[KageDesk] Successfully generated {written.Count} snapshots in '{outDir}'.");
                foreach (var file in written)
                {
                    Console.WriteLine($"  - {Path.GetFileName(file)}");
                }
                return 0;
            }

            if (string.Equals(args[0], "--record-gif", StringComparison.OrdinalIgnoreCase))
            {
                return HandleRecordGif(args);
            }
        }

        // 2. Single-Instance Mutex Enforcement for GUI
        using var mutex = new Mutex(true, SingleInstanceMutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            // Activate existing instance if possible
            WindowFocuser.TryFocusOrca();
            return 0;
        }

        try
        {
            // 3. Exception Handling & WinForms Bootstrap
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                // Safe logging without crashing desktop
            };
            Application.ThreadException += (_, e) =>
            {
                // Safe logging without crashing desktop
            };

            ApplicationConfiguration.Initialize();

            bool noWallpaper = args.Any(a => string.Equals(a, "--no-wallpaper", StringComparison.OrdinalIgnoreCase));
            using var runtime = new KageDeskRuntime(noWallpaper: noWallpaper);
            runtime.Start();

            Application.Run();
            return 0;
        }
        finally
        {
            if (isNewInstance)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch { }
            }
        }
    }

    internal static int HandleRecordGif(string[] args)
    {
        Win32Interop.AttachConsole(Win32Interop.ATTACH_PARENT_PROCESS);

        string? outPath = null;
        string themeId = "shinobi-hq";
        string scenario = "showcase";
        int scale = 1;

        var validScenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "showcase", "working", "handoff", "approve", "done", "fail", "clone", "variety", "mixi"
        };

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--theme", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                {
                    Console.Error.WriteLine("[KageDesk] Error: Missing value for --theme.");
                    return 2;
                }
                themeId = args[++i];
            }
            else if (arg.Equals("--scenario", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                {
                    Console.Error.WriteLine("[KageDesk] Error: Missing value for --scenario.");
                    return 2;
                }
                scenario = args[++i];
            }
            else if (arg.Equals("--scale", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                {
                    Console.Error.WriteLine("[KageDesk] Error: Missing value for --scale.");
                    return 2;
                }
                string scaleStr = args[++i];
                if (!int.TryParse(scaleStr, out int sc) || (sc != 1 && sc != 2))
                {
                    Console.Error.WriteLine($"[KageDesk] Error: Invalid scale '{scaleStr}'. Scale must be 1 or 2.");
                    return 2;
                }
                scale = sc;
            }
            else if (arg.Equals("--out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                {
                    Console.Error.WriteLine("[KageDesk] Error: Missing value for --out.");
                    return 2;
                }
                outPath = args[++i];
            }
            else if (arg.StartsWith("--"))
            {
                Console.Error.WriteLine($"[KageDesk] Error: Unknown flag '{arg}'.");
                return 2;
            }
            else
            {
                if (outPath == null)
                {
                    outPath = arg;
                }
                else
                {
                    Console.Error.WriteLine($"[KageDesk] Error: Unexpected argument '{arg}'.");
                    return 2;
                }
            }
        }

        outPath ??= Path.Combine(Directory.GetCurrentDirectory(), "kagedesk-demo.gif");

        if (outPath.EndsWith('/') || outPath.EndsWith('\\') || Directory.Exists(outPath))
        {
            Console.Error.WriteLine($"[KageDesk] Error: Destination path '{outPath}' is a directory.");
            return 2;
        }

        if (!validScenarios.Contains(scenario))
        {
            Console.Error.WriteLine($"[KageDesk] Error: Unknown scenario '{scenario}'. Valid scenarios: showcase, working, handoff, approve, done, fail, clone, variety, mixi.");
            return 2;
        }

        string themesDir = FindThemesDirectory();
        ThemeLoadResult themes;
        try
        {
            themes = ThemePackLoader.LoadThemes(themesDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[KageDesk] Error loading themes: {ex.Message}");
            return 1;
        }

        var activeTheme = themes.Themes.FirstOrDefault(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
        if (activeTheme == null)
        {
            string available = string.Join(", ", themes.Themes.Select(t => t.Id));
            Console.Error.WriteLine($"[KageDesk] Error: Theme '{themeId}' not found. Available themes: {available}");
            return 2;
        }

        try
        {
            Console.WriteLine($"[KageDesk] Recording 6s GIF ({DemoGifRecorder.TotalFrames} frames, scale={scale}x, theme='{activeTheme.Id}', scenario='{scenario}')...");
            DemoGifRecorder.RecordGif(outPath, activeTheme, scenario, scale, (curr, total) =>
            {
                if (curr % 12 == 0 || curr == total)
                {
                    Console.WriteLine($"[KageDesk] Frame {curr}/{total} encoded...");
                }
            });

            var fi = new FileInfo(outPath);
            Console.WriteLine($"[KageDesk] GIF generated successfully: {outPath} ({fi.Length / 1024.0:F1} KB)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[KageDesk] Error recording GIF: {ex.Message}");
            return 1;
        }
    }

    private static string FindThemesDirectory()
    {
        string[] candidates =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "themes"),
            Path.Combine(Directory.GetCurrentDirectory(), "themes"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "themes"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "themes")
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c))
            {
                return Path.GetFullPath(c);
            }
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "themes");
    }
}
