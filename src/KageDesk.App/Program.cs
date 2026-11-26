using KageDesk.App.Native;
using KageDesk.App.Services;
using KageDesk.Core.Config;
using KageDesk.Core.Hooks;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Export;
using KageDesk.Rendering.Snapshots;

namespace KageDesk.App;

internal static class Program
{
    public const string AppVersion = "2.2.0";
    private const string SingleInstanceMutexName = @"Local\KageDesk_SingleInstance_Mutex";

    internal static bool IsOneShotCliCommand(string[] args)
    {
        if (args == null || args.Length == 0) return false;
        string cmd = args[0];
        return string.Equals(cmd, "--benchmark", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "--snapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "--record-gif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "--setup-hooks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "--uninstall-hooks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "--version", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "-v", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cmd, "/?", StringComparison.OrdinalIgnoreCase);
    }

    internal static int HandleHookCommand(bool install, string? userProfileDir = null, string? appDataDir = null, int? bridgePort = null)
    {
        int port = bridgePort ?? new ConfigStore().Load().LocalBridgePort;
        var installer = new HookInstaller(userProfileDir: userProfileDir, appDataDir: appDataDir, bridgePort: port);
        var summary = install ? installer.InstallAll() : installer.UninstallAll();

        Console.WriteLine($"[KageDesk] {(install ? "Installing" : "Removing")} CLI hooks (bridge port {port}):");
        foreach (var r in summary.Results)
        {
            string backupNote = !string.IsNullOrEmpty(r.BackupPath) ? $" (backup: {r.BackupPath})" : "";
            Console.WriteLine($"  - {r.Target}: {r.Status} \u2014 {r.Message}{backupNote}");
        }

        return summary.AllSucceeded ? 0 : 1;
    }

    internal static int HandleVersionCommand()
    {
        Console.WriteLine($"KageDesk {AppVersion}");
        return 0;
    }

    internal static int HandleHelpCommand()
    {
        Console.WriteLine($"KageDesk {AppVersion} \u2014 Native Windows Pixel-Art Desktop HQ & Taskbar Pet");
        Console.WriteLine("Usage:");
        Console.WriteLine("  KageDesk.exe [--no-wallpaper]");
        Console.WriteLine("  KageDesk.exe --setup-hooks           Safely install hooks for Claude Code, Codex, and OpenCode");
        Console.WriteLine("  KageDesk.exe --uninstall-hooks       Cleanly remove KageDesk hooks only");
        Console.WriteLine("  KageDesk.exe --benchmark [sec]       Run headless render & resource benchmark");
        Console.WriteLine("  KageDesk.exe --snapshot [dir]        Export PNG preview snapshots");
        Console.WriteLine("  KageDesk.exe --record-gif [options]  Export a 6-second animated showcase GIF");
        Console.WriteLine("  KageDesk.exe --version | -v          Print version and exit");
        Console.WriteLine("  KageDesk.exe --help | -h             Print this help message and exit");
        return 0;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // 1. One-shot CLI Commands (run without acquiring GUI Single-Instance Mutex)
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase))
            {
                Win32Interop.AttachConsole(Win32Interop.ATTACH_PARENT_PROCESS);
                return HandleVersionCommand();
            }

            if (string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "/?", StringComparison.OrdinalIgnoreCase))
            {
                Win32Interop.AttachConsole(Win32Interop.ATTACH_PARENT_PROCESS);
                return HandleHelpCommand();
            }

            if (string.Equals(args[0], "--setup-hooks", StringComparison.OrdinalIgnoreCase))
            {
                Win32Interop.AttachConsole(Win32Interop.ATTACH_PARENT_PROCESS);
                return HandleHookCommand(install: true);
            }

            if (string.Equals(args[0], "--uninstall-hooks", StringComparison.OrdinalIgnoreCase))
            {
                Win32Interop.AttachConsole(Win32Interop.ATTACH_PARENT_PROCESS);
                return HandleHookCommand(install: false);
            }

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
            // Signal the running KageDesk instance via authenticated current-user Named Pipe IPC (bounded retry up to 2.5s)
            bool delivered = SingleInstanceWakeUp.TrySignalRunningInstance();
            if (!delivered)
            {
                Win32Interop.AttachConsole(Win32Interop.ATTACH_PARENT_PROCESS);
                Console.Error.WriteLine("[KageDesk] Another KageDesk instance is already running, but its wake-up pipe did not respond within 2.5s.");
                return 1;
            }
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
            "showcase", "working", "handoff", "drama", "celebrate", "approve", "done", "fail", "clone", "variety", "mixi"
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
            Console.Error.WriteLine($"[KageDesk] Error: Unknown scenario '{scenario}'. Valid scenarios: showcase, working, handoff, drama, celebrate, approve, done, fail, clone, variety, mixi.");
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
