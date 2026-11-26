using System.Drawing.Imaging;
using KageDesk.Core.Models;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;

namespace KageDesk.Rendering.Snapshots;

public static class PreviewSnapshotGenerator
{
    public static readonly string[] ExpectedFileNames =
    {
        "shinobi-hq-working.png",
        "shinobi-hq-approve.png",
        "shinobi-hq-done.png",
        "shinobi-hq-fail.png",
        "shinobi-hq-clone.png",
        "cyber-bunker-working.png",
        "cozy-lofi-idle.png",
        "pet-overlay-approve.png",
        "pet-overlay-working.png"
    };

    public static IReadOnlyList<string> GenerateAll(string outputDir, ThemeLoadResult themes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentNullException.ThrowIfNull(themes);

        Directory.CreateDirectory(outputDir);

        ThemePack shinobi = FindTheme(themes, "shinobi-hq");
        ThemePack cyber = FindTheme(themes, "cyber-bunker");
        ThemePack lofi = FindTheme(themes, "cozy-lofi");

        var written = new List<string>(ExpectedFileNames.Length);
        var fixedNow = new DateTimeOffset(2026, 11, 25, 14, 35, 0, TimeSpan.FromHours(7));

        using var hqCanvas = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var hqScaled = new PixelBuffer(HqWallpaperRenderer.CanvasWidth * 2, HqWallpaperRenderer.CanvasHeight * 2);
        var hqRenderer = new HqWallpaperRenderer();

        using var petCanvas = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        using var petScaled = new PixelBuffer(TaskbarPetRenderer.CanvasWidth * 2, TaskbarPetRenderer.CanvasHeight * 2);
        var petRenderer = new TaskbarPetRenderer();

        var previewStates = BuildPreviewStates(fixedNow);

        written.Add(RenderWallpaperSnapshot(hqRenderer, hqCanvas, hqScaled, previewStates["shinobi-hq-working.png"], shinobi, outputDir, "shinobi-hq-working.png", warmupTicks: 10));
        written.Add(RenderWallpaperSnapshot(hqRenderer, hqCanvas, hqScaled, previewStates["shinobi-hq-approve.png"], shinobi, outputDir, "shinobi-hq-approve.png", warmupTicks: 8));
        written.Add(RenderWallpaperSnapshot(hqRenderer, hqCanvas, hqScaled, previewStates["shinobi-hq-done.png"], shinobi, outputDir, "shinobi-hq-done.png", warmupTicks: 12));
        written.Add(RenderWallpaperSnapshot(hqRenderer, hqCanvas, hqScaled, previewStates["shinobi-hq-fail.png"], shinobi, outputDir, "shinobi-hq-fail.png", warmupTicks: 10));
        written.Add(RenderWallpaperSnapshot(hqRenderer, hqCanvas, hqScaled, previewStates["shinobi-hq-clone.png"], shinobi, outputDir, "shinobi-hq-clone.png", warmupTicks: 6));
        written.Add(RenderWallpaperSnapshot(hqRenderer, hqCanvas, hqScaled, previewStates["cyber-bunker-working.png"], cyber, outputDir, "cyber-bunker-working.png", warmupTicks: 10));
        written.Add(RenderWallpaperSnapshot(hqRenderer, hqCanvas, hqScaled, previewStates["cozy-lofi-idle.png"], lofi, outputDir, "cozy-lofi-idle.png", warmupTicks: 8));
        written.Add(RenderPetSnapshot(petRenderer, petCanvas, petScaled, previewStates["pet-overlay-approve.png"], shinobi, outputDir, "pet-overlay-approve.png", warmupTicks: 6));
        written.Add(RenderPetSnapshot(petRenderer, petCanvas, petScaled, previewStates["pet-overlay-working.png"], cyber, outputDir, "pet-overlay-working.png", warmupTicks: 8));

        return written;
    }

    internal static IReadOnlyDictionary<string, HqState> BuildPreviewStates(DateTimeOffset fixedNow)
    {
        var sampleAccounts = new List<AccountInfo>
        {
            new("claude", "hanphanr1", "hanphanr1@pro", true, QuotaPercent: 78),
            new("codex", "dev-codex", "dev-codex", false, QuotaPercent: 92),
            new("orca", "local", "orca-local", false)
        };

        var passingBuild = new List<BuildInfo>
        {
            new("ci-1", "kagedesk", "CI", "main", "Build #108", "completed", "success", UpdatedAt: fixedNow)
        };

        var failingBuild = new List<BuildInfo>
        {
            new("ci-fail", "kagedesk", "CI", "main", "Build #109", "completed", "failure", UpdatedAt: fixedNow)
        };

        var workingState = new HqState(
            Agents: new List<AgentSnapshot>
            {
                new("a-shika", "Shikamaru (Leader)", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "execute", Repo: "kagedesk"),
                new("a-minato", "Minato (Coder)", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit", Repo: "kagedesk"),
                new("a-tsunade", "Tsunade (Tester)", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test", Repo: "kagedesk"),
                new("a-kakashi", "Kakashi (Reviewer)", AgentRole.Reviewer, "kakashi", AgentState.Idle, Repo: "kagedesk")
            },
            Accounts: sampleAccounts,
            Builds: passingBuild,
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: fixedNow);

        var approveState = new HqState(
            Agents: new List<AgentSnapshot>
            {
                new("a-shika", "Shikamaru (Leader)", AgentRole.Leader, "shikamaru", AgentState.NeedsApproval,
                    QuestionPrompt: "Approve deploying KageDesk v2.1 to production release?", TaskTitle: "Release Gate", Repo: "kagedesk"),
                new("a-minato", "Minato (Coder)", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit", Repo: "kagedesk"),
                new("a-tsunade", "Tsunade (Tester)", AgentRole.Tester, "tsunade", AgentState.Idle, Repo: "kagedesk"),
                new("a-kakashi", "Kakashi (Reviewer)", AgentRole.Reviewer, "kakashi", AgentState.Idle, Repo: "kagedesk")
            },
            Accounts: sampleAccounts,
            Builds: new List<BuildInfo>
            {
                new("ci-wait", "kagedesk", "DEPLOY", "release", "Gate #12", "in_progress", null, UpdatedAt: fixedNow)
            },
            Weather: EffectiveWeather.Sunset,
            Mood: HqMood.Alert,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: fixedNow);

        var doneState = new HqState(
            Agents: new List<AgentSnapshot>
            {
                new("a-shika", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Done, TaskTitle: "Milestone 2 Complete!", Repo: "kagedesk"),
                new("a-minato", "Minato", AgentRole.Coder, "minato", AgentState.Done, TaskTitle: "Pixel Engine Green", Repo: "kagedesk"),
                new("a-tsunade", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Done, TaskTitle: "All Tests Passed", Repo: "kagedesk"),
                new("a-kakashi", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Done, TaskTitle: "LGTM Approved", Repo: "kagedesk")
            },
            Accounts: sampleAccounts,
            Builds: passingBuild,
            Weather: EffectiveWeather.Dawn,
            Mood: HqMood.Celebrating,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: fixedNow);

        var failState = new HqState(
            Agents: new List<AgentSnapshot>
            {
                new("a-shika", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Failed, LastMessage: "Build pipeline error", Repo: "kagedesk"),
                new("a-minato", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit", Repo: "kagedesk"),
                new("a-tsunade", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Failed, LastMessage: "3 tests failed", Repo: "kagedesk"),
                new("a-kakashi", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review", Repo: "kagedesk")
            },
            Accounts: new List<AccountInfo>
            {
                new("claude", "hanphanr1", "hanphanr1@pro", true, QuotaPercent: 14)
            },
            Builds: failingBuild,
            Weather: EffectiveWeather.StormAlert,
            Mood: HqMood.Failing,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: fixedNow);

        var cloneState = new HqState(
            Agents: new List<AgentSnapshot>
            {
                new("a-shika", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "execute", Repo: "kagedesk", Source: "orca", IsBotHome: true),
                new("a-minato", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit", Repo: "kagedesk", Source: "orca", IsBotHome: true),
                new("a-tsunade", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test", Repo: "kagedesk", Source: "orca", IsBotHome: true),
                new("a-kakashi", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review", Repo: "kagedesk", Source: "orca", IsBotHome: true),
                new("proc:omp:1001", "OMP", AgentRole.External, "dj-synth", AgentState.Working, ToolName: "edit", Source: "process", DeskRig: DeskRigKind.RgbStreamerRig, BehaviorVariant: 0),
                new("proc:claude:1002", "Claude", AgentRole.External, "neko-dev", AgentState.Working, ToolName: "test", Source: "process", DeskRig: DeskRigKind.CozyLofiRig, BehaviorVariant: 1),
                new("proc:codex:1003", "Codex", AgentRole.External, "cyber-hacker", AgentState.Working, ToolName: "exec", Source: "process", DeskRig: DeskRigKind.CyberConsoleRig, BehaviorVariant: 2)
            },
            Accounts: sampleAccounts,
            Builds: passingBuild,
            Weather: EffectiveWeather.MidnightRain,
            Mood: HqMood.Busy,
            PendingVisualEvents: new List<HqEvent>
            {
                new(1, "CloneSpawned", "Multi-terminal agents active", fixedNow, "proc:omp:1001")
            },
            Timestamp: fixedNow);

        var cyberWorkingState = workingState with { Weather = EffectiveWeather.MidnightRain };

        var lofiIdleState = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: new List<AccountInfo>
            {
                new("orca", "cozy", "lofi-studio", true)
            },
            Builds: passingBuild,
            Weather: EffectiveWeather.Sunset,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: fixedNow);

        return new Dictionary<string, HqState>(StringComparer.OrdinalIgnoreCase)
        {
            ["shinobi-hq-working.png"] = workingState,
            ["shinobi-hq-approve.png"] = approveState,
            ["shinobi-hq-done.png"] = doneState,
            ["shinobi-hq-fail.png"] = failState,
            ["shinobi-hq-clone.png"] = cloneState,
            ["cyber-bunker-working.png"] = cyberWorkingState,
            ["cozy-lofi-idle.png"] = lofiIdleState,
            ["pet-overlay-approve.png"] = approveState,
            ["pet-overlay-working.png"] = workingState
        };
    }

    private static string RenderWallpaperSnapshot(
        HqWallpaperRenderer renderer,
        PixelBuffer canvas,
        PixelBuffer upscaled,
        HqState state,
        ThemePack theme,
        string outputDir,
        string fileName,
        int warmupTicks)
    {
        renderer.ResetEffects();
        float t = 0.25f;
        for (int i = 0; i < warmupTicks; i++)
        {
            renderer.Render(canvas, state, theme, t, state.Timestamp.AddSeconds(t));
            t += 0.12f;
        }

        canvas.BlitNearestNeighborTo(upscaled, scale: 2);
        string path = Path.Combine(outputDir, fileName);
        upscaled.Bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static string RenderPetSnapshot(
        TaskbarPetRenderer renderer,
        PixelBuffer canvas,
        PixelBuffer upscaled,
        HqState state,
        ThemePack theme,
        string outputDir,
        string fileName,
        int warmupTicks)
    {
        renderer.ResetEffects();
        float t = 0.25f;
        for (int i = 0; i < warmupTicks; i++)
        {
            renderer.Render(canvas, state, theme, t, true, state.Timestamp.AddSeconds(t));
            t += 0.12f;
        }

        canvas.BlitNearestNeighborTo(upscaled, scale: 2);
        string path = Path.Combine(outputDir, fileName);
        upscaled.Bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static ThemePack FindTheme(ThemeLoadResult result, string id)
    {
        for (int i = 0; i < result.Themes.Count; i++)
        {
            if (string.Equals(result.Themes[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return result.Themes[i];
            }
        }
        return result.FallbackTheme;
    }
}
