using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using KageDesk.App.Native;
using KageDesk.Core.Bridge;
using KageDesk.Core.Collectors;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;

namespace KageDesk.App.Services;

public record BenchmarkResult(
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("workingSetMb")] double WorkingSetMb,
    [property: JsonPropertyName("peakWorkingSetMb")] double PeakWorkingSetMb,
    [property: JsonPropertyName("privateMemoryMb")] double PrivateMemoryMb,
    [property: JsonPropertyName("avgCpuPercent")] double AvgCpuPercent,
    [property: JsonPropertyName("gdiHandlesStart")] int GdiHandlesStart,
    [property: JsonPropertyName("gdiHandlesEnd")] int GdiHandlesEnd,
    [property: JsonPropertyName("gdiHandleDelta")] int GdiHandleDelta,
    [property: JsonPropertyName("framesRendered")] int FramesRendered,
    [property: JsonPropertyName("avgRenderMs")] double AvgRenderMs,
    [property: JsonPropertyName("orcaAgentsDetected")] int OrcaAgentsDetected,
    [property: JsonPropertyName("accountsDetected")] int AccountsDetected,
    [property: JsonPropertyName("themesLoaded")] int ThemesLoaded
);

public static class BenchmarkRunner
{
    public static async Task<int> RunAsync(double seconds, string? outPath = null)
    {
        double duration = Math.Max(1.0, seconds);
        var proc = Process.GetCurrentProcess();

        // 1. Load Themes
        string themesDir = FindThemesDirectory();
        var themeResult = ThemePackLoader.LoadThemes(themesDir);
        var theme = themeResult.Themes.Count > 0 ? themeResult.Themes[0] : ThemePackLoader.BuiltInFallback;

        // 2. Setup State Aggregator and Collectors
        var aggregator = new HqStateAggregator();
        var orcaCollector = new OrcaCliCollector(aggregator);

        // Run real Orca collector once
        await orcaCollector.CollectOnceAsync().ConfigureAwait(false);

        // 3. Setup LocalEventBridge on an ephemeral port
        int ephemeralPort = GetFreeTcpPort();
        using var bridge = new LocalEventBridge(aggregator, ephemeralPort);
        try
        {
            bridge.Start();
        }
        catch
        {
            // Ignore if port binding fails in benchmark
        }

        // 4. Setup Occlusion Monitor
        using var occlusionMonitor = new DesktopOcclusionMonitor(500);

        // 5. Setup Rendering Buffers & Persistent Layered DIB Surface
        using var wpBuf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petBuf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        using var dibSurface = new LayeredDibSurface(TaskbarPetRenderer.CanvasWidth * 2, TaskbarPetRenderer.CanvasHeight * 2);

        var wpRenderer = new HqWallpaperRenderer();
        var petRenderer = new TaskbarPetRenderer();

        // Warmup: run 5 frames and force GC to reach steady state
        for (int i = 0; i < 5; i++)
        {
            float t = i * 0.066f;
            var curState = aggregator.CurrentState;
            wpRenderer.Render(wpBuf, curState, theme, t);
            petRenderer.Render(petBuf, curState, theme, t);
            dibSurface.BlitNearestNeighborFrom(petBuf, 2);
            occlusionMonitor.CheckOcclusion();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Baseline measurements
        proc.Refresh();
        uint gdiStart = Win32Interop.GetGuiResources(proc.Handle, Win32Interop.GR_GDIOBJECTS);
        TimeSpan cpuStart = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();

        int frames = 0;
        double totalRenderMs = 0;
        var frameSw = new Stopwatch();
        var workingSetSamples = new List<double>();

        TimeSpan targetDuration = TimeSpan.FromSeconds(duration);

        while (sw.Elapsed < targetDuration)
        {
            frameSw.Restart();

            float t = (float)sw.Elapsed.TotalSeconds;
            var curState = aggregator.CurrentState;

            // Render wallpaper
            wpRenderer.Render(wpBuf, curState, theme, t);

            // Render pet & blit to persistent DIB surface (zero per-frame alloc)
            petRenderer.Render(petBuf, curState, theme, t);
            dibSurface.BlitNearestNeighborFrom(petBuf, 2);

            // Monitor check and natural Working Set sampling (~once per second / 15 frames)
            if (frames % 15 == 0)
            {
                occlusionMonitor.CheckOcclusion();
                proc.Refresh();
                workingSetSamples.Add(proc.WorkingSet64 / 1024.0 / 1024.0);
            }

            frameSw.Stop();
            totalRenderMs += frameSw.Elapsed.TotalMilliseconds;
            frames++;

            // Maintain ~14-15 FPS target for ultra-low CPU
            int elapsedMs = (int)frameSw.Elapsed.TotalMilliseconds;
            int sleepMs = 70 - elapsedMs;
            if (sleepMs > 1)
            {
                await Task.Delay(sleepMs).ConfigureAwait(false);
            }
        }

        sw.Stop();
        TimeSpan cpuEnd = proc.TotalProcessorTime;
        proc.Refresh();
        workingSetSamples.Add(proc.WorkingSet64 / 1024.0 / 1024.0);

        uint gdiEnd = Win32Interop.GetGuiResources(proc.Handle, Win32Interop.GR_GDIOBJECTS);
        int gdiDelta = (int)gdiEnd - (int)gdiStart;

        double wallMs = sw.Elapsed.TotalMilliseconds;
        double cpuMs = (cpuEnd - cpuStart).TotalMilliseconds;
        double avgCpuPercent = Math.Round((cpuMs / (wallMs * Math.Max(1, Environment.ProcessorCount))) * 100.0, 2);

        double workingSetMb = workingSetSamples.Count > 0
            ? Math.Round(workingSetSamples.Average(), 2)
            : Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 2);
        double peakWorkingSetMb = Math.Round(proc.PeakWorkingSet64 / 1024.0 / 1024.0, 2);
        double privateMemoryMb = Math.Round(proc.PrivateMemorySize64 / 1024.0 / 1024.0, 2);
        double avgRenderMs = frames > 0 ? Math.Round(totalRenderMs / frames, 2) : 0;

        var finalState = aggregator.CurrentState;
        var result = new BenchmarkResult(
            DurationSeconds: Math.Round(sw.Elapsed.TotalSeconds, 2),
            WorkingSetMb: workingSetMb,
            PeakWorkingSetMb: peakWorkingSetMb,
            PrivateMemoryMb: privateMemoryMb,
            AvgCpuPercent: avgCpuPercent,
            GdiHandlesStart: (int)gdiStart,
            GdiHandlesEnd: (int)gdiEnd,
            GdiHandleDelta: gdiDelta,
            FramesRendered: frames,
            AvgRenderMs: avgRenderMs,
            OrcaAgentsDetected: finalState.Agents.Count,
            AccountsDetected: finalState.Accounts.Count,
            ThemesLoaded: themeResult.Themes.Count
        );

        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // Write to stdout
        Console.WriteLine(json);

        // Write to --out file if specified
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(outPath, json);
        }

        return 0;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
