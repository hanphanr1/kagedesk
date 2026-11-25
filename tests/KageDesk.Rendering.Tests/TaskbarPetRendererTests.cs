using KageDesk.Core.Models;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;
using Xunit;

namespace KageDesk.Rendering.Tests;

public class TaskbarPetRendererTests
{
    private static HqState MakeState(params AgentSnapshot[] agents)
    {
        return new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Render_NeedsApprovalMode_RendersPillAndActionButtons()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var state = MakeState(
            new AgentSnapshot(
                Id: "agent-1",
                Name: "Shikamaru (Leader)",
                Role: AgentRole.Leader,
                SpriteKey: "shikamaru",
                State: AgentState.NeedsApproval,
                QuestionPrompt: "Approve deployment to production release?",
                TaskTitle: "Deploy Pipeline"));

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f);

        Assert.NotEmpty(regions);
        Assert.All(regions, AssertValidRegionInBounds);

        var openOrca = Assert.Single(regions, r => r.ActionId == "open_orca");
        var dismiss = Assert.Single(regions, r => r.ActionId == "dismiss");
        var agent = Assert.Single(regions, r => r.ActionId == "agent:agent-1");

        Assert.True(openOrca.Width > 60 && openOrca.Height > 10);
        Assert.True(dismiss.Width > 14 && dismiss.Height > 10);
        Assert.True(agent.Width >= 20 && agent.Height >= 20);

        Assert.True(ComputeTransparentRatio(buf) > 0.60);

        Assert.True(CountNonZeroPixelsInRegion(buf, openOrca.X, openOrca.Y, openOrca.Width, openOrca.Height) > 400);
        Assert.True(CountNonZeroPixelsInRegion(buf, dismiss.X, dismiss.Y, dismiss.Width, dismiss.Height) > 100);
    }

    [Fact]
    public void Render_NeedsApprovalOrca_AlongsideWorkingTerminal_EmitsOpenOrcaAndNeverFocusAgent()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var approvalAgent = new AgentSnapshot(
            Id: "orca-leader-1",
            Name: "Shikamaru",
            Role: AgentRole.Leader,
            SpriteKey: "shikamaru",
            State: AgentState.NeedsApproval,
            QuestionPrompt: "Approve deployment?",
            Source: "orca");

        var workingProcessAgent = new AgentSnapshot(
            Id: "proc:omp:1234abcd",
            Name: "OMP",
            Role: AgentRole.Coder,
            SpriteKey: "omp",
            State: AgentState.Working,
            ProcessId: 1234,
            Source: "process");

        var state = MakeState(approvalAgent, workingProcessAgent);

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f);

        // 1. Must render approval card with "open_orca" button
        var openOrca = Assert.Single(regions, r => r.ActionId == "open_orca");
        Assert.NotNull(openOrca);

        // 2. Must NEVER emit focus_agent: (approval takes priority, never routes to terminal window!)
        Assert.DoesNotContain(regions, r => r.ActionId.StartsWith("focus_agent:", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_WorkingMultipleAgents_RendersUpToSixMiniDesksAndAgentHitRegions()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var state = MakeState(
            new AgentSnapshot("a1", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit"),
            new AgentSnapshot("a2", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test"),
            new AgentSnapshot("a3", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review"),
            new AgentSnapshot("a4", "Clone #1", AgentRole.Clone, "clone", AgentState.Working, ToolName: "exec"));

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.5f);

        Assert.True(ComputeTransparentRatio(buf) > 0.70);
        Assert.Equal(4, regions.Count(r => r.ActionId.StartsWith("agent:", StringComparison.Ordinal)));
        Assert.Contains(regions, r => r.ActionId == "open_orca");
        Assert.All(regions, AssertValidRegionInBounds);
    }

    [Fact]
    public void Render_SixConcurrentWorkingAgents_FitsCleanlyWithoutClipping()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var state = MakeState(
            new AgentSnapshot("a1", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit"),
            new AgentSnapshot("a2", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test"),
            new AgentSnapshot("a3", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review"),
            new AgentSnapshot("a4", "OMP", AgentRole.Coder, "omp", AgentState.Working, ToolName: "code"),
            new AgentSnapshot("a5", "AGY", AgentRole.Leader, "agy", AgentState.Working, ToolName: "think"),
            new AgentSnapshot("a6", "Claude", AgentRole.Coder, "claude", AgentState.Working, ToolName: "exec"));

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f);

        Assert.Equal(6, regions.Count(r => r.ActionId.StartsWith("agent:", StringComparison.Ordinal)));
        Assert.Contains(regions, r => r.ActionId == "open_orca");
        Assert.All(regions, AssertValidRegionInBounds);
        Assert.True(ComputeTransparentRatio(buf) > 0.60);
    }

    [Fact]
    public void Render_MoreThanSixWorkingAgents_CapsAtSixAndShowsPlusN()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var agents = new List<AgentSnapshot>();
        for (int i = 1; i <= 8; i++)
        {
            agents.Add(new AgentSnapshot($"a{i}", $"Agent {i}", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit"));
        }

        var state = MakeState(agents.ToArray());
        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f);

        // Scratch caps rendered agents at 6
        Assert.Equal(6, regions.Count(r => r.ActionId.StartsWith("agent:", StringComparison.Ordinal)));
        Assert.Contains(regions, r => r.ActionId == "open_orca");
        Assert.All(regions, AssertValidRegionInBounds);
    }

    [Fact]
    public void Render_ReadFileTool_NeverProducesReadFil()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var state = MakeState(
            new AgentSnapshot("a1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "read_file"));

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f);

        Assert.Single(regions, r => r.ActionId == "agent:a1");
        var (kind, shortLabel) = AgentActionClassifier.Classify(AgentState.Working, "read_file");
        Assert.Equal(AgentActionKind.Reading, kind);
        Assert.Equal("READ", shortLabel);
        Assert.NotEqual("READ_FIL", shortLabel);
    }

    [Fact]
    public void Render_WorkingMode_HitRegionPill_ProcessAgentVsOrca()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        // Case 1: Single process working agent -> emits focus_agent:<id>
        var singleProcessState = MakeState(
            new AgentSnapshot("proc:omp:abcd1234", "OMP", AgentRole.Coder, "omp", AgentState.Working, Source: "process", ProcessId: 100));

        var regions1 = renderer.Render(buf, singleProcessState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.Contains(regions1, r => r.ActionId == "focus_agent:proc:omp:abcd1234");
        Assert.DoesNotContain(regions1, r => r.ActionId == "open_orca");

        // Case 2: Single Orca/non-process working agent -> emits open_orca
        var singleOrcaState = MakeState(
            new AgentSnapshot("a1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca"));

        var regions2 = renderer.Render(buf, singleOrcaState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.Contains(regions2, r => r.ActionId == "open_orca");
        Assert.DoesNotContain(regions2, r => r.ActionId.StartsWith("focus_agent:", StringComparison.Ordinal));

        // Case 3: Multiple working agents (even if processes) -> emits open_orca
        var multiState = MakeState(
            new AgentSnapshot("proc:omp:1", "OMP", AgentRole.Coder, "omp", AgentState.Working, Source: "process"),
            new AgentSnapshot("proc:agy:2", "AGY", AgentRole.Leader, "agy", AgentState.Working, Source: "process"));

        var regions3 = renderer.Render(buf, multiState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.Contains(regions3, r => r.ActionId == "open_orca");
        Assert.DoesNotContain(regions3, r => r.ActionId.StartsWith("focus_agent:", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_IdleMode_ShowsLiveIdleTerminalAgentsBesidePrimary()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var state = MakeState(
            new AgentSnapshot("proc:omp:100", "OMP", AgentRole.Coder, "omp", AgentState.Idle, Source: "process", ProcessId: 100),
            new AgentSnapshot("proc:agy:200", "AGY", AgentRole.Leader, "agy", AgentState.Idle, Source: "process", ProcessId: 200));

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f, showIdleChill: true);

        // Primary (Kakashi) + 2 live idle terminal agents
        Assert.Contains(regions, r => r.ActionId == "agent:proc:omp:100");
        Assert.Contains(regions, r => r.ActionId == "agent:proc:agy:200");
        Assert.All(regions, AssertValidRegionInBounds);
    }

    [Fact]
    public void Render_FailedAndDoneModes_RespectPriorityAndTransparency()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var failState = MakeState(
            new AgentSnapshot("w1", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("d1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Done),
            new AgentSnapshot("f1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Failed, LastMessage: "Build error"));

        IReadOnlyList<PetHitRegion> failRegions = renderer.Render(buf, failState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.True(ComputeTransparentRatio(buf) > 0.60);
        Assert.Contains(failRegions, r => r.ActionId == "agent:f1");
        Assert.Contains(failRegions, r => r.ActionId == "open_orca");
        Assert.Contains(failRegions, r => r.ActionId == "dismiss");

        var doneState = MakeState(
            new AgentSnapshot("d1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Done, TaskTitle: "All tests green"));
        IReadOnlyList<PetHitRegion> doneRegions = renderer.Render(buf, doneState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.True(ComputeTransparentRatio(buf) > 0.70);
        Assert.Contains(doneRegions, r => r.ActionId == "agent:d1");
    }

    [Fact]
    public void Render_IdleMode_HonorsShowIdleChillToggle()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var idleState = MakeState();

        IReadOnlyList<PetHitRegion> hiddenRegions = renderer.Render(buf, idleState, ThemePackLoader.BuiltInFallback, 0.5f, showIdleChill: false);
        Assert.Empty(hiddenRegions);
        Assert.Equal(1.0, ComputeTransparentRatio(buf));

        IReadOnlyList<PetHitRegion> chillRegions = renderer.Render(buf, idleState, ThemePackLoader.BuiltInFallback, 0.5f, showIdleChill: true);
        Assert.Single(chillRegions);
        double ratio = ComputeTransparentRatio(buf);
        Assert.True(ratio > 0.85 && ratio < 0.99, $"Expected idle chill transparent ratio between 85% and 99%, got {ratio:P1}.");
    }

    private static double ComputeTransparentRatio(PixelBuffer buf)
    {
        int transparent = 0;
        ReadOnlySpan<int> pixels = buf.ReadOnlyPixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] == 0) transparent++;
        }
        return (double)transparent / pixels.Length;
    }

    private static int CountNonZeroPixelsInRegion(PixelBuffer buf, int x, int y, int w, int h)
    {
        int count = 0;
        for (int py = y; py < y + h; py++)
        {
            for (int px = x; px < x + w; px++)
            {
                if (buf.GetPixel(px, py) != 0) count++;
            }
        }
        return count;
    }

    private static void AssertValidRegionInBounds(PetHitRegion r)
    {
        Assert.True(r.Width > 0 && r.Height > 0);
        Assert.True(r.X >= 0 && r.Y >= 0);
        Assert.True(r.X + r.Width <= TaskbarPetRenderer.CanvasWidth);
        Assert.True(r.Y + r.Height <= TaskbarPetRenderer.CanvasHeight);
    }
}
