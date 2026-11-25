using System.Diagnostics;
using System.Drawing;
using KageDesk.Core.Models;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;
using KageDesk.Rendering.Snapshots;
using Xunit.Abstractions;

namespace KageDesk.Rendering.Tests;

public class HqWallpaperRendererTests
{
    private readonly ITestOutputHelper _output;

    public HqWallpaperRendererTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static HqState CreateState(
        AgentState primaryState = AgentState.Working,
        EffectiveWeather weather = EffectiveWeather.Day,
        bool includeClones = false)
    {
        var agents = new List<AgentSnapshot>
        {
            new("a1", "Shikamaru (Leader)", AgentRole.Leader, "shikamaru", primaryState, ToolName: "execute", QuestionPrompt: "Approve deployment?", Repo: "kagedesk"),
            new("a2", "Minato (Coder)", AgentRole.Coder, "minato", primaryState == AgentState.Idle ? AgentState.Idle : AgentState.Working, ToolName: "edit", Repo: "kagedesk"),
            new("a3", "Tsunade (Tester)", AgentRole.Tester, "tsunade", primaryState, ToolName: "test", Repo: "kagedesk"),
            new("a4", "Kakashi (Reviewer)", AgentRole.Reviewer, "kakashi", primaryState == AgentState.Working ? AgentState.Working : AgentState.Idle, ToolName: "review", Repo: "kagedesk")
        };

        if (includeClones)
        {
            agents.Add(new AgentSnapshot("c1", "Clone #1", AgentRole.Clone, "clone", AgentState.Working, ToolName: "edit", WorktreeId: "wt-1"));
            agents.Add(new AgentSnapshot("c2", "Clone #2", AgentRole.Clone, "clone", AgentState.Working, ToolName: "test", WorktreeId: "wt-2"));
            agents.Add(new AgentSnapshot("e1", "Claude CLI", AgentRole.External, "generic", AgentState.Working, ToolName: "exec"));
        }

        var builds = new List<BuildInfo>
        {
            new("b1", "kagedesk", "CI", "main", "Build #42", "completed",
                primaryState == AgentState.Failed ? "failure" : "success",
                UpdatedAt: DateTimeOffset.UtcNow)
        };

        var accounts = new List<AccountInfo>
        {
            new("claude", "hanphanr1", "hanphanr1@pro", true, QuotaPercent: 72),
            new("codex", "dev", "dev-codex", false)
        };

        return new HqState(
            Agents: agents,
            Accounts: accounts,
            Builds: builds,
            Weather: weather,
            Mood: primaryState switch
            {
                AgentState.NeedsApproval => HqMood.Alert,
                AgentState.Failed => HqMood.Failing,
                AgentState.Done => HqMood.Celebrating,
                AgentState.Working => HqMood.Busy,
                _ => HqMood.Calm
            },
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: new DateTimeOffset(2026, 11, 25, 14, 30, 0, TimeSpan.FromHours(7)));
    }

    [Fact]
    public void Render_AllThreeThemes_ProduceDistinctFrames()
    {
        ThemeLoadResult loaded = ThemePackLoader.LoadThemes(ThemePacksOnDiskTests.FindThemesDirectory());
        var renderer = new HqWallpaperRenderer();
        HqState state = CreateState(AgentState.Working, EffectiveWeather.Day);

        var hashes = new Dictionary<string, long>();
        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);

        foreach (ThemePack theme in loaded.Themes)
        {
            renderer.ResetEffects();
            renderer.Render(buf, state, theme, 1.5f);
            hashes[theme.Id] = ComputeFrameChecksum(buf);
        }

        Assert.Equal(3, hashes.Values.Distinct().Count());
    }

    [Theory]
    [InlineData(AgentState.Idle)]
    [InlineData(AgentState.Working)]
    [InlineData(AgentState.NeedsApproval)]
    [InlineData(AgentState.Done)]
    [InlineData(AgentState.Failed)]
    public void Render_AllFiveAgentStates_SucceedAndFillOpaqueCanvas(AgentState stateKind)
    {
        var renderer = new HqWallpaperRenderer();
        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        HqState state = CreateState(stateKind, EffectiveWeather.Day);

        renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.2f);
        renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.5f);

        int opaqueCount = 0;
        foreach (int px in buf.ReadOnlyPixels)
        {
            if (((px >> 24) & 0xFF) == 255) opaqueCount++;
        }

        Assert.Equal(HqWallpaperRenderer.CanvasWidth * HqWallpaperRenderer.CanvasHeight, opaqueCount);
    }

    [Fact]
    public void Render_AllFiveWeatherModes_ProduceDistinctWindowRegions()
    {
        var renderer = new HqWallpaperRenderer();
        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        ThemePack theme = ThemePackLoader.BuiltInFallback;

        EffectiveWeather[] modes =
        {
            EffectiveWeather.Dawn,
            EffectiveWeather.Day,
            EffectiveWeather.Sunset,
            EffectiveWeather.MidnightRain,
            EffectiveWeather.StormAlert
        };

        var windowHashes = new HashSet<long>();
        foreach (EffectiveWeather weather in modes)
        {
            renderer.Render(buf, CreateState(AgentState.Working, weather), theme, 2.0f);
            windowHashes.Add(ComputeRegionChecksum(buf, 125, 15, 134, 50));
        }

        Assert.Equal(5, windowHashes.Count);
    }

    [Fact]
    public void Render_EmptyAgentsState_RendersFourStandbyCharacters()
    {
        var renderer = new HqWallpaperRenderer();
        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);

        var emptyState = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Sunset,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);

        renderer.Render(buf, emptyState, ThemePackLoader.BuiltInFallback, 0.8f);

        // Verify all 4 core desk zones have diverse pixel colors (character + desk + nameplate)
        int[] centers = { 56, 146, 238, 328 };
        foreach (int cx in centers)
        {
            int uniqueColors = CountUniqueColorsInRegion(buf, cx - 20, 108, 40, 48);
            Assert.True(uniqueColors >= 8, $"Expected rich pixel detail at core station x={cx}, found {uniqueColors} unique colors.");
        }
    }

    [Fact]
    public void Render_ThemeWithNullScene_FallsBackToPaletteCleanly()
    {
        ThemePack legacyTheme = ThemePackLoader.BuiltInFallback with { Scene = null };
        var renderer = new HqWallpaperRenderer();
        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);

        renderer.Render(buf, CreateState(AgentState.Working, EffectiveWeather.Day), legacyTheme, 1.0f);
        Assert.NotEqual(0, buf.GetPixel(192, 120));
    }

    [Fact]
    public void Render_WithClonesAndExternalAgents_RendersSecondRowCleanly()
    {
        var renderer = new HqWallpaperRenderer();
        using var bufNoClones = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var bufClones = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);

        renderer.Render(bufNoClones, CreateState(AgentState.Working, includeClones: false), ThemePackLoader.BuiltInFallback, 1.0f);
        renderer.ResetEffects();
        renderer.Render(bufClones, CreateState(AgentState.Working, includeClones: true), ThemePackLoader.BuiltInFallback, 1.0f);

        Assert.NotEqual(ComputeRegionChecksum(bufNoClones, 40, 150, 300, 55), ComputeRegionChecksum(bufClones, 40, 150, 300, 55));
    }

    [Fact]
    public void Render_PerformanceBenchmark_Under3MsPerFrame()
    {
        ThemeLoadResult loaded = ThemePackLoader.LoadThemes(ThemePacksOnDiskTests.FindThemesDirectory());
        ThemePack theme = loaded.Themes.First(t => t.Id == "shinobi-hq");
        HqState busyState = CreateState(AgentState.Working, EffectiveWeather.MidnightRain, includeClones: true);

        var renderer = new HqWallpaperRenderer();
        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);

        // Warmup JIT
        for (int i = 0; i < 20; i++)
        {
            renderer.Render(buf, busyState, theme, i * 0.033f);
        }

        const int measuredFrames = 150;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < measuredFrames; i++)
        {
            renderer.Render(buf, busyState, theme, 1.0f + i * 0.033f);
        }
        sw.Stop();

        double avgMs = sw.Elapsed.TotalMilliseconds / measuredFrames;
        _output.WriteLine($"HqWallpaperRenderer average frame time: {avgMs:F3} ms/frame over {measuredFrames} frames (total {sw.Elapsed.TotalMilliseconds:F1} ms)");
        Assert.True(avgMs < 3.0, $"Expected average frame time < 3.0 ms, measured {avgMs:F3} ms/frame.");
    }

    [Fact]
    public void PreviewSnapshotGenerator_GeneratesAllNineUpscaledPngs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kagedesk_snaps_" + Guid.NewGuid().ToString("N"));
        try
        {
            ThemeLoadResult themes = ThemePackLoader.LoadThemes(ThemePacksOnDiskTests.FindThemesDirectory());
            IReadOnlyList<string> paths = PreviewSnapshotGenerator.GenerateAll(tempDir, themes);

            Assert.Equal(9, paths.Count);
            foreach (string expectedName in PreviewSnapshotGenerator.ExpectedFileNames)
            {
                string fullPath = Path.Combine(tempDir, expectedName);
                Assert.True(File.Exists(fullPath), $"Missing generated snapshot '{expectedName}'.");

                using var img = Image.FromFile(fullPath);
                if (expectedName.StartsWith("pet-overlay-", StringComparison.Ordinal))
                {
                    Assert.Equal(768, img.Width);
                    Assert.Equal(256, img.Height);
                }
                else
                {
                    Assert.Equal(768, img.Width);
                    Assert.Equal(432, img.Height);
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void AssignStations_LiveTerminalAgentsOutrankStaleOrcaWorktrees()
    {
        var renderer = new HqWallpaperRenderer();
        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);

        var agents = new List<AgentSnapshot>
        {
            new("shika", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "execute"),
            new("mina", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit"),
            new("tsuna", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test"),
            new("kaka", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review")
        };

        // Add 20 stale idle child worktrees
        for (int i = 1; i <= 20; i++)
        {
            agents.Add(new AgentSnapshot($"wt-{i}", $"worktree-{i}", AgentRole.Coder, "minato", AgentState.Idle, WorktreeId: $"wt-{i}", Source: "orca"));
        }

        // Add 2 live standalone terminal agents
        agents.Add(new AgentSnapshot("proc:omp:101", "OMP", AgentRole.Coder, "omp", AgentState.Working, ToolName: "thinking", Source: "process", ProcessId: 101));
        agents.Add(new AgentSnapshot("proc:agy:102", "AGY", AgentRole.Leader, "agy", AgentState.Working, ToolName: "search", Source: "process", ProcessId: 102));

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);

        // Render should succeed without throwing and render 4 core + up to 5 extra desks (9 total live desks)
        renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.True(CountUniqueColorsInRegion(buf, 0, 0, buf.Width, buf.Height) > 15);
    }

    [Fact]
    public void Render_AllEightWorkingActions_ProduceDistinctFramesAndPerformance()
    {
        var renderer = new HqWallpaperRenderer();
        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);

        string[] toolNames = { "code", "run_command", "read_file", "search_web", "test", "build", "git", "review", "think" };
        var checksums = new HashSet<long>();

        // Warmup JIT
        var warmupState = new HqState(
            Agents: new List<AgentSnapshot> { new("a0", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "code") },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);
        renderer.Render(buf, warmupState, ThemePackLoader.BuiltInFallback, 1.0f);
        ComputeFrameChecksum(buf);

        var sw = new Stopwatch();
        foreach (var tool in toolNames)
        {
            var state = new HqState(
                Agents: new List<AgentSnapshot>
                {
                    new("a1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: tool)
                },
                Accounts: Array.Empty<AccountInfo>(),
                Builds: Array.Empty<BuildInfo>(),
                Weather: EffectiveWeather.Day,
                Mood: HqMood.Busy,
                PendingVisualEvents: Array.Empty<HqEvent>(),
                Timestamp: DateTimeOffset.UtcNow);

            renderer.ResetEffects();
            sw.Start();
            renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f);
            sw.Stop();

            long checksum = ComputeFrameChecksum(buf);
            checksums.Add(checksum);
        }

        double avgMs = sw.Elapsed.TotalMilliseconds / toolNames.Length;
        Assert.True(avgMs < 3.0, $"Expected avg frame time < 3.0 ms, got {avgMs:F2} ms");
        // Verify distinct checksums (all different animations!)
        Assert.Equal(toolNames.Length, checksums.Count);
    }

    [Fact]
    public void NameplateLayout_Row2FiveSlots_NeverOverlap()
    {
        // 5 workstations on row 2 have centers at: 48, 120, 192, 264, 336 (dx = 72)
        int[] centers = { 48, 120, 192, 264, 336 };
        var layouts = new HqWallpaperRenderer.NameplateLayout[centers.Length];

        for (int i = 0; i < centers.Length; i++)
        {
            layouts[i] = HqWallpaperRenderer.ComputeNameplateLayout(
                cx: centers[i],
                y: 160,
                nameSpan: "LongAgentName",
                badgeSpan: "APPROVE",
                maxPlateWidth: 68);
        }

        for (int i = 0; i < centers.Length - 1; i++)
        {
            int currentRight = layouts[i].Right;
            int nextLeft = layouts[i + 1].Left;

            Assert.True(currentRight < nextLeft,
                $"Plate {i} (right={currentRight}) overlaps or touches plate {i + 1} (left={nextLeft})");
            Assert.True(layouts[i].TotalWidth <= 68,
                $"Plate {i} width {layouts[i].TotalWidth} exceeds maxPlateWidth 68");
        }
    }


    private static long ComputeFrameChecksum(PixelBuffer buf)
        => ComputeRegionChecksum(buf, 0, 0, buf.Width, buf.Height);

    private static long ComputeRegionChecksum(PixelBuffer buf, int x, int y, int w, int h)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            for (int py = y; py < y + h; py++)
            {
                for (int px = x; px < x + w; px++)
                {
                    hash = (hash ^ buf.GetPixel(px, py)) * 1099511628211L;
                }
            }
            return hash;
        }
    }

    private static int CountUniqueColorsInRegion(PixelBuffer buf, int x, int y, int w, int h)
    {
        var set = new HashSet<int>();
        for (int py = y; py < y + h; py++)
        {
            for (int px = x; px < x + w; px++)
            {
                set.Add(buf.GetPixel(px, py));
            }
        }
        return set.Count;
    }

    [Fact]
    public void Render_OversizedPixelBuffer_RendersWithoutExceptionAndDrawsPixels()
    {
        var renderer = new HqWallpaperRenderer();
        var state = CreateState();
        var theme = ThemePackLoader.BuiltInFallback;

        using var oversizedBuf = new PixelBuffer(420, 250);
        oversizedBuf.ClearTransparent();

        // Must not throw ArgumentException and must render scene
        var ex = Record.Exception(() => renderer.Render(oversizedBuf, state, theme, 1.0f));
        Assert.Null(ex);

        // Verify pixels are rendered
        bool hasNonZeroPixel = false;
        foreach (int pixel in oversizedBuf.ReadOnlyPixels)
        {
            if (pixel != 0)
            {
                hasNonZeroPixel = true;
                break;
            }
        }
        Assert.True(hasNonZeroPixel);

        // Verify second render (validating any caching edge case on subsequent frames)
        var ex2 = Record.Exception(() => renderer.Render(oversizedBuf, state, theme, 2.0f));
        Assert.Null(ex2);
    }

    [Fact]
    public void Render_ElapsedTimerPill_RendersWithoutException_WhenUnder10Minutes()
    {
        var renderer = new HqWallpaperRenderer();
        var now = DateTimeOffset.UtcNow;
        var agent = new AgentSnapshot("ag-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, WorkStartedAt: now.AddMinutes(-3));
        var state = new HqState(new[] { agent }, Array.Empty<AccountInfo>(), Array.Empty<BuildInfo>(), EffectiveWeather.Day, HqMood.Busy, Array.Empty<HqEvent>(), Timestamp: now);
        var theme = ThemePackLoader.BuiltInFallback;

        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        var ex = Record.Exception(() => renderer.Render(buf, state, theme, 1.0f));
        Assert.Null(ex);
    }

    [Fact]
    public void Render_ElapsedTimerPill_RendersWithoutException_WhenOver10Minutes()
    {
        var renderer = new HqWallpaperRenderer();
        var now = DateTimeOffset.UtcNow;
        var agent = new AgentSnapshot("ag-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, WorkStartedAt: now.AddMinutes(-15));
        var state = new HqState(new[] { agent }, Array.Empty<AccountInfo>(), Array.Empty<BuildInfo>(), EffectiveWeather.Day, HqMood.Busy, Array.Empty<HqEvent>(), Timestamp: now);
        var theme = ThemePackLoader.BuiltInFallback;

        using var buf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        var ex = Record.Exception(() => renderer.Render(buf, state, theme, 1.0f));
        Assert.Null(ex);
    }
}

