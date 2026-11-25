using System.IO;
using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;

namespace KageDesk.Rendering.Export;

public static class DemoGifRecorder
{
    public const int TotalFrames = 75; // 6.00 seconds @ 12.5 FPS (75 frames * 8 cs = 600 cs)
    public const int FrameDelayCentiseconds = 8; // 80ms delay = 12.5 FPS (6.00s total)

    public static void RecordGif(
        string outputPath,
        ThemePack theme,
        string scenario = "showcase",
        int scale = 1,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(theme);

        cancellationToken.ThrowIfCancellationRequested();

        string fullOutPath = Path.GetFullPath(outputPath);
        string? dir = Path.GetDirectoryName(fullOutPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = fullOutPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var encoder = new Gif89aEncoder(fileStream, HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight, scale);
                var renderer = new HqWallpaperRenderer();
                using var pixelBuffer = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);

                DateTimeOffset baseTime = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
                string scenKey = scenario.Trim().ToLowerInvariant();
                int[] scratch = new int[HqWallpaperRenderer.CanvasWidth * HqWallpaperRenderer.CanvasHeight];

                for (int frame = 0; frame < TotalFrames; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    float timeSeconds = frame * (6.0f / TotalFrames);
                    DateTimeOffset frameNow = baseTime.AddSeconds(timeSeconds);

                    HqState state = BuildFrameState(scenKey, frame, timeSeconds, frameNow, baseTime);

                    renderer.Render(pixelBuffer, state, theme, timeSeconds, nowOverride: frameNow);

                    pixelBuffer.ReadOnlyPixels.CopyTo(scratch);
                    encoder.AddFrame(scratch, FrameDelayCentiseconds, cancellationToken);

                    onProgress?.Invoke(frame + 1, TotalFrames);
                }

                cancellationToken.ThrowIfCancellationRequested();
                encoder.Finish();
            }

            File.Move(tmpPath, fullOutPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch
            {
                // Ignore secondary cleanup error
            }

            throw;
        }
    }

    private static HqState BuildFrameState(
        string scenario,
        int frame,
        float timeSeconds,
        DateTimeOffset now,
        DateTimeOffset baseTime)
    {
        var agents = new List<AgentSnapshot>();
        var builds = new List<BuildInfo>();
        var visualEvents = new List<HqEvent>();
        var accounts = new List<AccountInfo>
        {
            new("anthropic", "claude-3-7-sonnet", "Claude 3.7", true, 82)
        };
        EffectiveWeather weather = EffectiveWeather.Day;
        HqMood mood = HqMood.Calm;

        switch (scenario)
        {
            case "handoff":
            {
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", TaskTitle: "Refactoring state engine", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-65)));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test", TaskTitle: "Integration testing", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-15)));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true));

                visualEvents.Add(new HqEvent(1, "handoff", "Scroll handoff from Minato to Tsunade", baseTime.AddSeconds(0.2), "demo-coder", 10.0, "demo-tester"));
                mood = HqMood.Busy;
                break;
            }

            case "working":
            {
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "execute", TaskTitle: "Orchestrating agents", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-120)));
                agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", TaskTitle: "Async pipeline", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-620)));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review", TaskTitle: "Code review", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-45)));
                mood = HqMood.Busy;
                break;
            }

            case "approve":
            {
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.NeedsApproval, QuestionPrompt: "Approve deployment to production?", TaskTitle: "Deploy Gate", IsBotHome: true, Unread: true, WorkStartedAt: baseTime.AddSeconds(-35)));
                agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "git", TaskTitle: "Ready to push", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-180)));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true));
                visualEvents.Add(new HqEvent(1, "ApprovalRaised", "Shikamaru needs approval", baseTime, "demo-leader", 15.0));
                mood = HqMood.Alert;
                break;
            }

            case "done":
            {
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Done, TaskTitle: "Release v2.0 complete", UpdatedAt: baseTime, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Done, TaskTitle: "All tests passing", UpdatedAt: baseTime, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Done, TaskTitle: "QA verified", UpdatedAt: baseTime, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Done, TaskTitle: "Review passed", UpdatedAt: baseTime, IsBotHome: true));
                builds.Add(new BuildInfo("build-1", "kagedesk", "CI", "main", "Build #108", "completed", "success", UpdatedAt: baseTime));
                mood = HqMood.Celebrating;
                break;
            }

            case "fail":
            {
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Failed, LastMessage: "Build failed: syntax error", IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "debug", TaskTitle: "Fixing issue", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-20)));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true));
                builds.Add(new BuildInfo("build-1", "kagedesk", "CI", "main", "Build #108", "completed", "failure", UpdatedAt: baseTime));
                weather = EffectiveWeather.StormAlert;
                mood = HqMood.Alert;
                break;
            }

            case "clone":
            {
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "command", TaskTitle: "Leader", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-40)));
                agents.Add(new AgentSnapshot("demo-clone-1", "Shadow Clone #1", AgentRole.Clone, "clone", AgentState.Working, ToolName: "worker", WorktreeId: "wt:worker-1", WorkStartedAt: baseTime.AddSeconds(-30)));
                agents.Add(new AgentSnapshot("demo-clone-2", "Shadow Clone #2", AgentRole.Clone, "clone", AgentState.Working, ToolName: "worker", WorktreeId: "wt:worker-2", WorkStartedAt: baseTime.AddSeconds(-20)));
                agents.Add(new AgentSnapshot("demo-clone-3", "Shadow Clone #3", AgentRole.Clone, "clone", AgentState.Working, ToolName: "worker", WorktreeId: "wt:worker-3", WorkStartedAt: baseTime.AddSeconds(-15)));
                agents.Add(new AgentSnapshot("demo-clone-4", "Shadow Clone #4", AgentRole.Clone, "clone", AgentState.Working, ToolName: "worker", WorktreeId: "wt:worker-4", WorkStartedAt: baseTime.AddSeconds(-10)));
                mood = HqMood.Busy;
                break;
            }

            case "showcase":
            default:
            {
                // Multi-phase choreographed 6-second showcase:
                // Phase 1 (0.0 .. 1.25s, frames 0..15): Working agents with cyan elapsed timer pills
                // Phase 2 (1.25 .. 4.25s, frames 16..53): Living handoff (Minato walks across rug with scroll & Hiraishin sparks to Tsunade)
                // Phase 3 (4.25 .. 5.25s, frames 54..65): Celebration sparks / done
                // Phase 4 (5.25 .. 6.00s, frames 66..74): Calm return to idle desks
                if (timeSeconds < 1.25f)
                {
                    agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "plan", TaskTitle: "Architecture plan", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-45)));
                    agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", TaskTitle: "Building v2.0 features", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-95)));
                    agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true));
                    agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true));
                    mood = HqMood.Busy;
                }
                else if (timeSeconds < 4.25f)
                {
                    agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle, IsBotHome: true));
                    agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", TaskTitle: "Handoff to QA", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-120)));
                    agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test", TaskTitle: "Receiving test scroll", IsBotHome: true, WorkStartedAt: baseTime.AddSeconds(-10)));
                    agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true));

                    visualEvents.Add(new HqEvent(1, "handoff", "Scroll handoff from Minato to Tsunade", baseTime.AddSeconds(1.25), "demo-coder", 10.0, "demo-tester"));
                    mood = HqMood.Busy;
                }
                else if (timeSeconds < 5.25f)
                {
                    agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Done, TaskTitle: "Release ready", UpdatedAt: baseTime.AddSeconds(4.25), IsBotHome: true));
                    agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Done, TaskTitle: "Handoff complete", UpdatedAt: baseTime.AddSeconds(4.25), IsBotHome: true));
                    agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Done, TaskTitle: "All tests pass", UpdatedAt: baseTime.AddSeconds(4.25), IsBotHome: true));
                    agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Done, TaskTitle: "Approved", UpdatedAt: baseTime.AddSeconds(4.25), IsBotHome: true));
                    builds.Add(new BuildInfo("build-1", "kagedesk", "CI", "main", "Build #200", "completed", "success", UpdatedAt: baseTime.AddSeconds(4.25)));
                    mood = HqMood.Celebrating;
                }
                else
                {
                    agents.Add(new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle, IsBotHome: true));
                    agents.Add(new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Idle, IsBotHome: true));
                    agents.Add(new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true));
                    agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true));
                    mood = HqMood.Calm;
                }
                break;
            }
        }

        return new HqState(
            Agents: agents,
            Accounts: accounts,
            Builds: builds,
            Weather: weather,
            Mood: mood,
            PendingVisualEvents: visualEvents.ToArray(),
            DemoScenario: scenario,
            Timestamp: now
        );
    }
}
