using KageDesk.App.Native;
using KageDesk.App.Services;
using KageDesk.Core.Themes;
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
        }

        // 2. Single-Instance Mutex Enforcement for GUI
        using var mutex = new Mutex(true, SingleInstanceMutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            // Activate existing instance if possible
            WindowFocuser.TryFocusOrca();
            return 0;
        }

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
