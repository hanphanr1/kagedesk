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
        Assert.True(openOrca.Width > 0 && openOrca.Height > 0);

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

        // Case 1: Single verified process working agent -> emits focus_agent:<id>
        var singleProcessState = MakeState(
            new AgentSnapshot("proc:omp:abcd1234", "OMP", AgentRole.Coder, "omp", AgentState.Working, Source: "process", ProcessId: 100, ProcessStartTicks: 1000));

        var regions1 = renderer.Render(buf, singleProcessState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.Contains(regions1, r => r.ActionId == "focus_agent:proc:omp:abcd1234");
        Assert.DoesNotContain(regions1, r => r.ActionId == "open_orca");

        // Case 1b: Process agent with unverified StartTimeTicks (0 or null) -> MUST NOT emit focus_agent:
        var unverifiedProcessState = MakeState(
            new AgentSnapshot("proc:omp:unverified", "OMP", AgentRole.Coder, "omp", AgentState.Working, Source: "process", ProcessId: 100, ProcessStartTicks: 0));
        var regionsUnverified = renderer.Render(buf, unverifiedProcessState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.DoesNotContain(regionsUnverified, r => r.ActionId.StartsWith("focus_agent:", StringComparison.Ordinal));
        Assert.Contains(regionsUnverified, r => r.ActionId == "agent:proc:omp:unverified");

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

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f, showIdleChill: true, showMixiWhenAllIdle: false);

        // Primary (Kakashi) + 2 live idle terminal agents when Do Mixi idle mode is disabled
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

    [Fact]
    public void Render_WorkingModeWithElapsedTimer_RendersPillWithoutException()
    {
        var renderer = new TaskbarPetRenderer();
        var now = DateTimeOffset.UtcNow;
        var agent = new AgentSnapshot("ag-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", WorkStartedAt: now.AddSeconds(-102));
        var state = new HqState(new[] { agent }, Array.Empty<AccountInfo>(), Array.Empty<BuildInfo>(), EffectiveWeather.Day, HqMood.Busy, Array.Empty<HqEvent>(), Timestamp: now);
        var theme = ThemePackLoader.BuiltInFallback;

        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var ex = Record.Exception(() => renderer.Render(buf, state, theme, 1.0f));
        Assert.Null(ex);
        Assert.NotEmpty(renderer.HitRegions);
    }

    [Fact]
    public void Render_WorkingMode_AllFiveMiniDeskRigs_ProduceDistinctFrames()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var checksums = new HashSet<long>();

        foreach (DeskRigKind rig in AgentAppearanceRegistry.RandomizableDeskRigs)
        {
            var state = MakeState(
                new AgentSnapshot("proc:omp:petrig", "OMP", AgentRole.External, "dj-synth", AgentState.Working, ToolName: "edit", Source: "process", DeskRig: rig));

            renderer.ResetEffects();
            renderer.Render(buf, state, theme, 1.0f);
            checksums.Add(ComputeChecksum(buf));
        }

        Assert.Equal(5, checksums.Count);
    }

    [Fact]
    public void Render_WorkingMode_AllEightNewCharacters_ProduceDistinctFrames()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var checksums = new HashSet<long>();

        foreach (string sprite in AgentAppearanceRegistry.RandomizableSprites)
        {
            var state = MakeState(
                new AgentSnapshot($"proc:omp:{sprite}", "OMP", AgentRole.External, sprite, AgentState.Working, ToolName: "edit", Source: "process", DeskRig: DeskRigKind.DualMonitorRig));

            renderer.ResetEffects();
            renderer.Render(buf, state, theme, 1.0f);
            checksums.Add(ComputeChecksum(buf));
        }

        Assert.Equal(8, checksums.Count);
    }

    [Fact]
    public void Render_LivingHandoff_WalksHorizontallyWithScrollMinatoFlashAndReturnsToDesk()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var now = DateTimeOffset.UtcNow;

        var agents = new[]
        {
            new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit", Source: "orca", IsBotHome: true, WorkStartedAt: now),
            new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test", Source: "orca", IsBotHome: true, WorkStartedAt: now)
        };

        var handoffEvent = new HqEvent(
            SequenceId: 101,
            Type: "handoff",
            Message: "Minato -> Tsunade: PR ready",
            Timestamp: now,
            AgentId: "demo-coder",
            TargetAgentId: "demo-tester",
            FromIsConcreteId: true,
            ToIsConcreteId: true);

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: new[] { handoffEvent },
            Timestamp: now);

        bool sawWalking = false;
        bool sawMinatoFlashTrail = false;
        bool sawScroll = false;
        bool sawDelivering = false;
        bool sawWalkingBack = false;
        int minOffsetX = 0;
        int maxOffsetX = 0;
        long baselineChecksum = 0;
        long midWalkChecksum = 0;

        for (int frame = 0; frame <= 60; frame++)
        {
            float t = frame * 0.1f;
            var regions = renderer.Render(buf, state, theme, t, showIdleChill: true, nowOverride: now.AddSeconds(t));
            Assert.All(regions, AssertValidRegionInBounds);
            Assert.True(ComputeTransparentRatio(buf) > 0.55, $"Frame {frame}: expected transparent background ratio > 0.55");

            if (frame == 0) baselineChecksum = ComputeChecksum(buf);

            int activeIdx = renderer.MotionSystem.ActiveHandoffStationIndex;
            if (activeIdx >= 0)
            {
                var m = renderer.MotionSystem.GetMotion(activeIdx);
                minOffsetX = Math.Min(minOffsetX, m.OffsetX);
                maxOffsetX = Math.Max(maxOffsetX, m.OffsetX);

                if (m.State == KageDesk.Rendering.Sprites.ActorMotionState.WalkingToTarget)
                {
                    sawWalking = true;
                    if (m.CarryingScroll) sawScroll = true;
                    if (m.IsFlashTrail) sawMinatoFlashTrail = true;
                    if (midWalkChecksum == 0 && Math.Abs(m.OffsetX) > 12)
                    {
                        midWalkChecksum = ComputeChecksum(buf);
                    }
                }
                else if (m.State == KageDesk.Rendering.Sprites.ActorMotionState.Delivering)
                {
                    sawDelivering = true;
                }
                else if (m.State == KageDesk.Rendering.Sprites.ActorMotionState.WalkingBack)
                {
                    sawWalkingBack = true;
                }
            }
        }

        Assert.True(sawWalking, "Expected sender to enter WalkingToTarget state on Taskbar Pet.");
        Assert.True(sawScroll, "Expected walking sender to carry glowing scroll on Taskbar Pet.");
        Assert.True(sawMinatoFlashTrail, "Expected Minato sender to trigger golden Hiraishin flash trail on Taskbar Pet.");
        Assert.True(sawDelivering, "Expected sender to pause and deliver at recipient desk.");
        Assert.True(sawWalkingBack, "Expected sender to walk back to own desk after delivery.");
        Assert.True(maxOffsetX - minOffsetX >= 30, $"Expected clear horizontal walk travel across taskbar strip, got span {maxOffsetX - minOffsetX}px.");
        Assert.NotEqual(0, midWalkChecksum);
        Assert.NotEqual(baselineChecksum, midWalkChecksum);
        Assert.False(renderer.IsHandoffActive, "Expected handoff to complete and return cleanly to AtDesk.");
    }

    [Fact]
    public void Render_LivingHandoff_GuaranteesBothSenderAndRecipientInStrip_EvenWithMoreThanSixAgentsOrDoneSender()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var now = DateTimeOffset.UtcNow;

        // 7 working agents + 1 Done sender ("sender-done") handing off to the 7th working agent ("recipient-late")
        var agents = new[]
        {
            new AgentSnapshot("w1", "OMP 1", AgentRole.External, "dj-synth", AgentState.Working, Source: "process"),
            new AgentSnapshot("w2", "OMP 2", AgentRole.External, "neko-dev", AgentState.Working, Source: "process"),
            new AgentSnapshot("w3", "OMP 3", AgentRole.External, "cyber-hacker", AgentState.Working, Source: "process"),
            new AgentSnapshot("w4", "OMP 4", AgentRole.External, "ronin-dev", AgentState.Working, Source: "process"),
            new AgentSnapshot("w5", "OMP 5", AgentRole.External, "astro-dev", AgentState.Working, Source: "process"),
            new AgentSnapshot("w6", "OMP 6", AgentRole.External, "wizard-dev", AgentState.Working, Source: "process"),
            new AgentSnapshot("recipient-late", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca", IsBotHome: true),
            new AgentSnapshot("sender-done", "Minato", AgentRole.Coder, "minato", AgentState.Done, Source: "orca", IsBotHome: true)
        };

        var handoffEvent = new HqEvent(
            SequenceId: 202,
            Type: "handoff",
            Message: "Minato -> Tsunade: Verify",
            Timestamp: now,
            AgentId: "sender-done",
            TargetAgentId: "recipient-late",
            FromIsConcreteId: true,
            ToIsConcreteId: true);

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: new[] { handoffEvent },
            Timestamp: now);

        var regions = renderer.Render(buf, state, theme, 0.2f, showIdleChill: true, nowOverride: now);
        Assert.True(renderer.IsHandoffActive);
        Assert.Contains(regions, r => r.ActionId == "agent:sender-done");
        Assert.Contains(regions, r => r.ActionId == "agent:recipient-late");
        Assert.All(regions, AssertValidRegionInBounds);
    }

    [Fact]
    public void Render_LivingHandoff_ConcreteRecipientDepartsMidWalk_WalksBackWithoutDeliveringToReplacement()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var now = DateTimeOffset.UtcNow;

        var initialAgents = new[]
        {
            new AgentSnapshot("sender-1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca", IsBotHome: true),
            new AgentSnapshot("recipient-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca", IsBotHome: true)
        };

        var handoffEvent = new HqEvent(
            SequenceId: 303,
            Type: "handoff",
            Message: "Minato -> Tsunade",
            Timestamp: now,
            AgentId: "sender-1",
            TargetAgentId: "recipient-1",
            FromIsConcreteId: true,
            ToIsConcreteId: true);

        var stateInitial = new HqState(
            Agents: initialAgents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: new[] { handoffEvent },
            Timestamp: now);

        // Advance into WalkingToTarget
        for (int i = 0; i < 6; i++)
        {
            renderer.Render(buf, stateInitial, theme, i * 0.1f, showIdleChill: true, nowOverride: now.AddSeconds(i * 0.1));
        }
        Assert.True(renderer.IsHandoffActive);

        // Now recipient-1 disappears and is replaced by a different concrete agent id with the same sprite
        var replacementState = stateInitial with
        {
            Agents = new[]
            {
                new AgentSnapshot("sender-1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca", IsBotHome: true),
                new AgentSnapshot("recipient-other", "Tsunade 2", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca", IsBotHome: true)
            },
            PendingVisualEvents = Array.Empty<HqEvent>()
        };

        bool everDelivered = false;
        bool sawWalkBack = false;
        for (int i = 6; i < 40; i++)
        {
            renderer.Render(buf, replacementState, theme, i * 0.1f, showIdleChill: true, nowOverride: now.AddSeconds(i * 0.1));
            int activeIdx = renderer.MotionSystem.ActiveHandoffStationIndex;
            if (activeIdx >= 0)
            {
                var st = renderer.MotionSystem.GetMotion(activeIdx).State;
                if (st == KageDesk.Rendering.Sprites.ActorMotionState.Delivering) everDelivered = true;
                if (st == KageDesk.Rendering.Sprites.ActorMotionState.WalkingBack) sawWalkBack = true;
            }
        }

        Assert.False(everDelivered, "Concrete recipient vanished — sender must NOT deliver to a replacement agent.");
        Assert.True(sawWalkBack, "Expected sender to smoothly walk back to desk when concrete recipient departed.");
    }

    [Fact]
    public void Render_AllIdle_WithShowMixiEnabled_RendersExclusiveDoMixiAcrossAllFiveActivities()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;

        var idleState = MakeState(
            new AgentSnapshot("o1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle, Source: "orca", IsBotHome: true),
            new AgentSnapshot("o2", "Minato", AgentRole.Coder, "minato", AgentState.Idle, Source: "orca", IsBotHome: true));

        var checksums = new HashSet<long>();
        var labels = new HashSet<string>();
        var activities = new HashSet<KageDesk.Rendering.Sprites.MixiChillActivity>();
        float[] sampleTimes = { 1.0f, 9.0f, 17.0f, 25.0f, 33.0f };

        foreach (float t in sampleTimes)
        {
            var regions = renderer.Render(buf, idleState, theme, t, showIdleChill: true, showMixiWhenAllIdle: true);
            Assert.True(renderer.IsMixiChillActive);
            Assert.NotNull(renderer.ActiveMixiActivity);
            activities.Add(renderer.ActiveMixiActivity!.Value);

            var hit = Assert.Single(regions);
            Assert.Equal("agent:mixi-chill", hit.ActionId);
            Assert.StartsWith("DO MIXI", hit.Label);
            AssertValidRegionInBounds(hit);
            Assert.True(ComputeTransparentRatio(buf) > 0.65);

            labels.Add(hit.Label!);
            checksums.Add(ComputeChecksum(buf));
        }

        Assert.Equal(5, activities.Count);
        Assert.Equal(5, labels.Count);
        Assert.Equal(5, checksums.Count);
    }

    [Fact]
    public void Render_AllIdleToActiveTransition_ImmediatelyHidesDoMixi()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;

        var allIdleState = MakeState(
            new AgentSnapshot("o1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle, Source: "orca", IsBotHome: true));

        renderer.Render(buf, allIdleState, theme, 1.0f, showIdleChill: true, showMixiWhenAllIdle: true);
        Assert.True(renderer.IsMixiChillActive);

        // Any active agent (Orca or CLI) immediately hides Do Mixi
        var workingState = MakeState(
            new AgentSnapshot("o1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle, Source: "orca", IsBotHome: true),
            new AgentSnapshot("proc:omp:1", "OMP", AgentRole.External, "dj-synth", AgentState.Working, ToolName: "edit", Source: "process"));

        var workingRegions = renderer.Render(buf, workingState, theme, 2.0f, showIdleChill: true, showMixiWhenAllIdle: true);
        Assert.False(renderer.IsMixiChillActive);
        Assert.DoesNotContain(workingRegions, r => r.ActionId == "agent:mixi-chill");
        Assert.Contains(workingRegions, r => r.ActionId == "agent:proc:omp:1");

        // DemoScenario == "mixi" forces Do Mixi Chill Mode even if showMixiWhenAllIdle == false
        var demoMixiState = allIdleState with { DemoScenario = "mixi" };
        var demoRegions = renderer.Render(buf, demoMixiState, theme, 3.0f, showIdleChill: true, showMixiWhenAllIdle: false);
        Assert.True(renderer.IsMixiChillActive);
        Assert.Contains(demoRegions, r => r.ActionId == "agent:mixi-chill");
    }

    [Fact]
    public void Render_IdleMode_MixiDisabled_IdleCanonicalBotRendersMatchingSpriteAndHitTarget()
    {
        var renderer = new TaskbarPetRenderer();
        using var bufMinato = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        using var bufTsunade = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;

        var idleMinato = MakeState(
            new AgentSnapshot("minato-1", "Minato", AgentRole.Coder, "minato", AgentState.Idle, Source: "orca", IsBotHome: true));
        var minatoRegions = renderer.Render(bufMinato, idleMinato, theme, 1.0f, showIdleChill: true, showMixiWhenAllIdle: false);
        Assert.False(renderer.IsMixiChillActive);
        Assert.Equal("minato", renderer.LastRenderedIdlePrimarySprite);
        Assert.Contains(minatoRegions, r => r.ActionId == "agent:minato-1");

        var idleTsunade = MakeState(
            new AgentSnapshot("tsunade-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle, Source: "orca", IsBotHome: true));
        var tsunadeRegions = renderer.Render(bufTsunade, idleTsunade, theme, 1.0f, showIdleChill: true, showMixiWhenAllIdle: false);
        Assert.False(renderer.IsMixiChillActive);
        Assert.Equal("tsunade", renderer.LastRenderedIdlePrimarySprite);
        Assert.Contains(tsunadeRegions, r => r.ActionId == "agent:tsunade-1");

        Assert.NotEqual(ComputeChecksum(bufMinato), ComputeChecksum(bufTsunade));
    }

    [Fact]
    public void Render_ActiveHandoff_BothEndpointsVanishAndZeroWorkers_ClearsMotionAndUnblocksMixi()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var now = DateTimeOffset.UtcNow;

        var handoffState = new HqState(
            Agents: new[]
            {
                new AgentSnapshot("minato-1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca"),
                new AgentSnapshot("tsunade-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca")
            },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: new[] { new HqEvent(1, "handoff", "Pass tests", now, "minato-1", 10.0, "tsunade-1") },
            Timestamp: now);

        renderer.Render(buf, handoffState, theme, 0.1f, showIdleChill: true, nowOverride: now, showMixiWhenAllIdle: true);
        renderer.Render(buf, handoffState, theme, 0.5f, showIdleChill: true, nowOverride: now, showMixiWhenAllIdle: true);
        Assert.True(renderer.IsHandoffActive);
        Assert.False(renderer.IsMixiChillActive);

        // Both endpoints vanish leaving 0 working agents -> motion MUST clear cleanly and unblock Mixi!
        var emptyState = handoffState with { Agents = Array.Empty<AgentSnapshot>(), Mood = HqMood.Calm };
        renderer.Render(buf, emptyState, theme, 0.6f, showIdleChill: true, nowOverride: now.AddSeconds(0.5), showMixiWhenAllIdle: true);

        Assert.False(renderer.IsHandoffActive);
        Assert.True(renderer.IsMixiChillActive);
    }

    [Fact]
    public void Render_ActiveHandoff_StripShrinksTwoToOneWhenRecipientDeparts_SenderWalksBackSmoothlyToSlot0()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var now = DateTimeOffset.UtcNow;

        // Recipient is slot 0 (x=346), Sender is slot 1 (x=278)
        var handoffState = new HqState(
            Agents: new[]
            {
                new AgentSnapshot("tsunade-0", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca"),
                new AgentSnapshot("minato-1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca")
            },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: new[] { new HqEvent(1, "handoff", "Hand off", now, "minato-1", 10.0, "tsunade-0") },
            Timestamp: now);

        // Advance into WalkingToTarget
        renderer.Render(buf, handoffState, theme, 0.05f, nowOverride: now);
        renderer.Render(buf, handoffState, theme, 0.45f, nowOverride: now);
        renderer.Render(buf, handoffState, theme, 0.60f, nowOverride: now);
        Assert.True(renderer.IsHandoffActive);

        // Recipient departs, leaving only sender in a 1-slot strip (slot 0)
        var oneSlotState = handoffState with
        {
            Agents = new[]
            {
                new AgentSnapshot("minato-1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca")
            }
        };

        // Immediately after shrink: sender MUST still be actively walking back (NOT abruptly canceled!)
        renderer.Render(buf, oneSlotState, theme, 0.66f, nowOverride: now);
        Assert.True(renderer.IsHandoffActive);

        // Advance until walk-back completes cleanly
        for (int i = 1; i <= 35; i++)
        {
            renderer.Render(buf, oneSlotState, theme, 0.66f + i * 0.1f, nowOverride: now.AddSeconds(i * 0.1));
        }

        Assert.False(renderer.IsHandoffActive);
        Assert.Contains(renderer.HitRegions, r => r.ActionId == "agent:minato-1");
    }

    [Fact]
    public void Render_BackToBackHandoffEvents_FirstEndsOrCancels_SecondEventPrioritizedAndVisiblyAnimated()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var now = DateTimeOffset.UtcNow;

        // 8 working agents: event #1 is w1 -> w2; event #2 is w7 -> w8 (initially outside the top-6 strip!)
        var agents = new List<AgentSnapshot>
        {
            new("w1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca"),
            new("w2", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca"),
            new("w3", "Worker 3", AgentRole.Clone, "dj-synth", AgentState.Working, Source: "orca"),
            new("w4", "Worker 4", AgentRole.Clone, "neko-dev", AgentState.Working, Source: "orca"),
            new("w5", "Worker 5", AgentRole.Clone, "cyber-hacker", AgentState.Working, Source: "orca"),
            new("w6", "Worker 6", AgentRole.Clone, "ronin-dev", AgentState.Working, Source: "orca"),
            new("w7", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, Source: "orca"),
            new("w8", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, Source: "orca")
        };

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: new[]
            {
                new HqEvent(1, "handoff", "First handoff", now, "w1", 20.0, "w2"),
                new HqEvent(2, "handoff", "Second handoff", now, "w7", 20.0, "w8")
            },
            Timestamp: now);

        // Start event #1 (w1 -> w2)
        renderer.Render(buf, state, theme, 0.05f, nowOverride: now);
        Assert.True(renderer.IsHandoffActive);
        Assert.Contains(renderer.HitRegions, r => r.ActionId == "agent:w1");
        Assert.Contains(renderer.HitRegions, r => r.ActionId == "agent:w2");

        // Advance past event #1 natural completion (or cancellation): event #2 must NOT be dropped against old layout!
        // Instead, once event #1 finishes, w7 and w8 MUST be prioritized into the visible strip and event #2 MUST animate!
        bool sawSecondHandoffAnimated = false;
        for (int step = 1; step <= 60; step++)
        {
            float t = 0.05f + step * 0.08f;
            var regions = renderer.Render(buf, state, theme, t, nowOverride: now.AddSeconds(t));
            if (renderer.IsHandoffActive &&
                regions.Any(r => r.ActionId == "agent:w7") &&
                regions.Any(r => r.ActionId == "agent:w8"))
            {
                sawSecondHandoffAnimated = true;
                break;
            }
        }

        Assert.True(sawSecondHandoffAnimated, "Second handoff endpoints (w7, w8) must be prioritized into the visible strip and visibly animated after first handoff completes.");

        // Case B: First handoff cancels mid-flight when sender vanishes -> second event MUST be prioritized and visibly animated!
        var cancelRenderer = new TaskbarPetRenderer();
        cancelRenderer.Render(buf, state, theme, 0.05f, nowOverride: now);
        cancelRenderer.Render(buf, state, theme, 0.45f, nowOverride: now.AddSeconds(0.4));
        Assert.True(cancelRenderer.IsHandoffActive);

        // Sender w1 disappears mid-handoff while event #2 (w7 -> w8) is pending
        var afterCancelState = state with { Agents = agents.Where(a => a.Id != "w1").ToList() };
        var regionsAfterCancel = cancelRenderer.Render(buf, afterCancelState, theme, 0.55f, nowOverride: now.AddSeconds(0.5));
        Assert.True(cancelRenderer.IsHandoffActive);
        Assert.Contains(regionsAfterCancel, r => r.ActionId == "agent:w7");
        Assert.Contains(regionsAfterCancel, r => r.ActionId == "agent:w8");
    }

    [Fact]
    public void Render_ActiveHandoffDelivering_OverlapHitTestSelectsForegroundSenderOverRecipient()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var now = DateTimeOffset.UtcNow;

        // Slot 0 (x=346): Minato (sender); Slot 1 (x=278): Tsunade (recipient)
        var handoffState = new HqState(
            Agents: new[]
            {
                new AgentSnapshot("minato-sender", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca"),
                new AgentSnapshot("tsunade-recipient", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca")
            },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: new[] { new HqEvent(1, "handoff", "Deliver scroll", now, "minato-sender", 10.0, "tsunade-recipient") },
            Timestamp: now);

        // Advance into Delivering phase where foreground sender stands right next to recipient (overlapping X range)
        IReadOnlyList<PetHitRegion> regions = Array.Empty<PetHitRegion>();
        for (int i = 0; i < 20; i++)
        {
            float t = 0.05f + i * 0.06f;
            regions = renderer.Render(buf, handoffState, theme, t, nowOverride: now.AddSeconds(t));
            var s = regions.FirstOrDefault(r => r.ActionId == "agent:minato-sender");
            var rcp = regions.FirstOrDefault(r => r.ActionId == "agent:tsunade-recipient");
            if (s.Width > 0 && rcp.Width > 0)
            {
                int overlapLeft = Math.Max(s.X, rcp.X);
                int overlapRight = Math.Min(s.X + s.Width, rcp.X + rcp.Width);
                if (overlapRight > overlapLeft)
                {
                    int overlapX = (overlapLeft + overlapRight) / 2;
                    int overlapY = Math.Max(s.Y, rcp.Y) + 10;

                    var hit = renderer.HitTest(overlapX, overlapY);
                    Assert.True(hit.HasValue);
                    Assert.Equal("agent:minato-sender", hit.Value.ActionId);
                    return;
                }
            }
        }

        Assert.Fail("Expected foreground sender and stationary recipient hit regions to overlap during handoff delivery.");
    }

    [Fact]
    public void Render_SameCountWorkingSlotReorder_UpdatesHitTargetsImmediately()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;

        var agentA = new AgentSnapshot("agent-a", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca");
        var agentB = new AgentSnapshot("agent-b", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca");

        // Render [A, B]: A at slot 0 (x=346), B at slot 1 (x=278)
        var stateAB = MakeState(agentA, agentB);
        renderer.Render(buf, stateAB, theme, 1.0f);
        Assert.Equal("agent:agent-a", renderer.HitTest(346, 105)?.ActionId);
        Assert.Equal("agent:agent-b", renderer.HitTest(278, 105)?.ActionId);

        // Reorder with SAME count (2): [B, A] -> hit targets MUST update immediately!
        var stateBA = stateAB with { Agents = new[] { agentB, agentA } };
        renderer.Render(buf, stateBA, theme, 1.1f);
        Assert.Equal("agent:agent-b", renderer.HitTest(346, 105)?.ActionId);
        Assert.Equal("agent:agent-a", renderer.HitTest(278, 105)?.ActionId);
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("waiting-for-input")]
    [InlineData("waiting_for_input")]
    [InlineData("needs_you")]
    [InlineData("blocked")]
    public void Render_UpstreamOrcaLifecycleStates_BlockMixiChillMode(string rawState)
    {
        var now = DateTimeOffset.UtcNow;
        string json = $$"""
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "wt:active",
                "repo": "kagedesk",
                "displayName": "Minato \u00B7 Active",
                "status": "{{rawState}}",
                "agents": [
                  {
                    "address": "orca_session_id:active-1",
                    "state": "{{rawState}}",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var parsed = KageDesk.Core.Collectors.OrcaSnapshotParser.ParseWorktreePs(json, now);
        var state = MakeState(parsed.ToArray());

        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f, showIdleChill: true, nowOverride: now, showMixiWhenAllIdle: true);

        Assert.False(renderer.IsMixiChillActive);
    }

    [Fact]
    public void Render_AlreadyConsumedHandoffStillWithinExpiry_DoesNotSuppressMixiAfterMotionFinishes()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var theme = ThemePackLoader.BuiltInFallback;
        var now = DateTimeOffset.UtcNow;

        // Long-lived handoff event (60s duration) that lingers in PendingVisualEvents after motion completes (~2.5s)
        var handoffEvt = new HqEvent(42, "handoff", "Quick pass", now, "minato-1", 60.0, "tsunade-1");
        var workingState = new HqState(
            Agents: new[]
            {
                new AgentSnapshot("minato-1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca"),
                new AgentSnapshot("tsunade-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, Source: "orca")
            },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: new[] { handoffEvt },
            Timestamp: now);

        // Start and run the handoff motion completely to finish (~3 seconds)
        for (int i = 0; i < 40; i++)
        {
            float t = 0.05f + i * 0.1f;
            renderer.Render(buf, workingState, theme, t, showIdleChill: true, nowOverride: now.AddSeconds(t), showMixiWhenAllIdle: true);
        }

        Assert.False(renderer.IsHandoffActive);
        Assert.False(renderer.HasUnconsumedHandoffEvent(workingState));

        // Now all agents go Idle, while the already-consumed handoffEvt STILL lingers in PendingVisualEvents (well within 60s expiry)
        var idleWithLingeringEvent = workingState with
        {
            Agents = new[]
            {
                new AgentSnapshot("minato-1", "Minato", AgentRole.Coder, "minato", AgentState.Idle, Source: "orca"),
                new AgentSnapshot("tsunade-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle, Source: "orca")
            },
            Mood = HqMood.Calm
        };

        renderer.Render(buf, idleWithLingeringEvent, theme, 5.0f, showIdleChill: true, nowOverride: now.AddSeconds(5.0), showMixiWhenAllIdle: true);
        Assert.True(renderer.IsMixiChillActive);
    }

    [Fact]
    public void PetHitRegion_IsValueType_ZeroReferenceAllocationPerHitRegion()
    {
        Assert.True(typeof(PetHitRegion).IsValueType);
    }

    private static long ComputeChecksum(PixelBuffer buf)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            var pixels = buf.ReadOnlyPixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                hash = (hash ^ pixels[i]) * 1099511628211L;
            }
            return hash;
        }
    }
}
