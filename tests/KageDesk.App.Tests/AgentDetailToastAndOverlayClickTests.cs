using System.Drawing;
using System.Windows.Forms;
using KageDesk.App.Native;
using KageDesk.App.UI;
using KageDesk.Core.Models;
using KageDesk.Core.Themes;
using Xunit;

namespace KageDesk.App.Tests;

[Collection("WinFormsGdi")]
public class AgentDetailToastAndOverlayClickTests
{
    [Fact]
    public void AgentDetailToast_FormatSourceLabel_FormatsProcessAndOrcaSourcesCorrectly()
    {
        // 1. Process sources
        var ompAg = new AgentSnapshot("p1", "OMP", AgentRole.Coder, "omp", AgentState.Working, Source: "process", ProcessId: 1010);
        Assert.Equal("OMP Terminal  |  PID: 1010", AgentDetailToast.FormatSourceLabel(ompAg));

        var agyAg = new AgentSnapshot("p2", "AGY", AgentRole.Leader, "agy", AgentState.Working, Source: "process", ProcessId: 2020);
        Assert.Equal("AGY Terminal  |  PID: 2020", AgentDetailToast.FormatSourceLabel(agyAg));

        var claudeAg = new AgentSnapshot("p3", "Claude", AgentRole.Reviewer, "claude", AgentState.Working, Source: "process", ProcessId: 3030);
        Assert.Equal("Claude Terminal  |  PID: 3030", AgentDetailToast.FormatSourceLabel(claudeAg));

        var codexAg = new AgentSnapshot("p4", "Codex", AgentRole.Tester, "codex", AgentState.Working, Source: "process", ProcessId: 4040);
        Assert.Equal("Codex Terminal  |  PID: 4040", AgentDetailToast.FormatSourceLabel(codexAg));

        var opencodeAg = new AgentSnapshot("p5", "OpenCode", AgentRole.Coder, "opencode", AgentState.Working, Source: "process", ProcessId: 5050);
        Assert.Equal("OpenCode Terminal  |  PID: 5050", AgentDetailToast.FormatSourceLabel(opencodeAg));

        var geminiAg = new AgentSnapshot("p6", "Gemini", AgentRole.Reviewer, "gemini", AgentState.Working, Source: "process", ProcessId: 6060);
        Assert.Equal("Gemini Terminal  |  PID: 6060", AgentDetailToast.FormatSourceLabel(geminiAg));

        var genericProc = new AgentSnapshot("p7", "Generic", AgentRole.External, "unknown_sprite", AgentState.Working, Source: "process", ProcessId: 7070);
        Assert.Equal("Terminal Agent  |  PID: 7070", AgentDetailToast.FormatSourceLabel(genericProc));

        // 2. Orca sources
        var orcaWithRepo = new AgentSnapshot("o1", "Minato", AgentRole.Coder, "minato", AgentState.Working, Source: "orca", Repo: "kagedesk");
        Assert.Equal("Orca Bot  |  Repo: kagedesk", AgentDetailToast.FormatSourceLabel(orcaWithRepo));

        var orcaNoRepo = new AgentSnapshot("o2", "Kakashi", AgentRole.Leader, "kakashi", AgentState.Idle, Source: "orca");
        Assert.Equal("Orca Bot", AgentDetailToast.FormatSourceLabel(orcaNoRepo));
    }

    [Fact]
    public void AgentDetailToast_RandomizedSpriteKeys_StillResolveCleanTerminalProviderAndNeverUseClone()
    {
        var mixiOmp = new AgentSnapshot("proc:omp:abcd1234", "OMP", AgentRole.Coder, "mixi", AgentState.Working, Source: "process", ProcessId: 6660, DeskRig: DeskRigKind.RgbStreamerRig);
        Assert.Equal("OMP", AgentDetailToast.ResolveTerminalProvider(mixiOmp));
        Assert.Equal("OMP Terminal  |  PID: 6660", AgentDetailToast.FormatSourceLabel(mixiOmp));

        var nekoClaude = new AgentSnapshot("proc:claude:ef567890", "Claude 2", AgentRole.Coder, "neko-dev", AgentState.Idle, Source: "process", ProcessId: 7770, DeskRig: DeskRigKind.CozyLofiRig);
        Assert.Equal("Claude", AgentDetailToast.ResolveTerminalProvider(nekoClaude));
        Assert.Equal("Claude Terminal  |  PID: 7770", AgentDetailToast.FormatSourceLabel(nekoClaude));

        var sanitized = AgentAppearanceRegistry.SanitizeDisplayName("Clone H1", "proc:omp:abcd1234", "process");
        Assert.Equal("OMP", sanitized);
        Assert.DoesNotContain("clone", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentDetailToast_SingleInstanceReplacement_AndLifecycle()
    {
        var snap1 = new AgentSnapshot("t1", "Agent One", AgentRole.Leader, "kakashi", AgentState.Working);
        var snap2 = new AgentSnapshot("t2", "Agent Two", AgentRole.Coder, "minato", AgentState.Working);

        AgentDetailToast.ShowToast(snap1);
        var first = AgentDetailToast.ActiveToast;
        Assert.NotNull(first);
        Assert.False(first.IsDisposed);

        // Showing second toast must close and dispose the first, leaving only the second active
        AgentDetailToast.ShowToast(snap2);
        var second = AgentDetailToast.ActiveToast;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);

        // Closing the second toast must clear ActiveToast to null
        second.Close();
        Assert.Null(AgentDetailToast.ActiveToast);
    }

    [Fact]
    public void PetOverlayForm_IdleFallbackClick_ConstructsValidFallbackAndInvokesAgentClickedOnce()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        using var form = new PetOverlayForm(theme, initialScale: 1);

        // Empty agent state in chill mode
        var emptyState = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>()
        );

        // Test standard fallback idle click when Do Mixi idle mode is disabled
        form.SetShowMixiWhenAllIdle(false);
        form.ApplyRenderState(emptyState, theme, petPaused: false, petHiddenByDndOrLock: false);

        int invokeCount = 0;
        AgentSnapshot? capturedAgent = null;
        form.AgentClicked += ag =>
        {
            invokeCount++;
            capturedAgent = ag;
        };

        // In idle chill mode, primary idle pet hit region is centered around X=344, Y=120 (bounds: 328, 90, 32, 32)
        form.SimulateMouseDown(340, 105, MouseButtons.Left);

        Assert.Equal(1, invokeCount);
        Assert.NotNull(capturedAgent);
        Assert.Equal(AgentState.Idle, capturedAgent.State);
        Assert.Equal("fallback", capturedAgent.Source);
        Assert.False(string.IsNullOrWhiteSpace(capturedAgent.Name));
        Assert.False(string.IsNullOrWhiteSpace(capturedAgent.Id));
    }

    [Fact]
    public void PetOverlayForm_AllIdleMixiClick_InvokesAgentClickedWithDoMixiChillSnapshot()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        using var form = new PetOverlayForm(theme, initialScale: 1, idleMode: "chill", showMixiWhenAllIdle: true);
        form.Start();

        var emptyState = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>()
        );

        form.ApplyRenderState(emptyState, theme, petPaused: false, petHiddenByDndOrLock: false);
        Assert.True(form.IsMixiChillActive);

        AgentSnapshot? capturedAgent = null;
        form.AgentClicked += ag => capturedAgent = ag;

        // Click inside Do Mixi chill corner region (centered near X=330, Y=95)
        form.SimulateMouseDown(330, 95, MouseButtons.Left);

        Assert.NotNull(capturedAgent);
        Assert.Equal("mixi-chill", capturedAgent.Id);
        Assert.Equal("Do Mixi Gaming", capturedAgent.Name);
        Assert.Equal("idle-chill", capturedAgent.Source);
        Assert.Equal("All-Idle Chill Corner", AgentDetailToast.FormatSourceLabel(capturedAgent));
    }

    [Fact]
    public void WindowFocuser_VerificationHelpers_ValidatePidAndParentAge()
    {
        // 1. IsPidAgeValid
        Assert.True(WindowFocuser.IsPidAgeValid(123456789L, 123456789L));
        Assert.False(WindowFocuser.IsPidAgeValid(123456789L, 987654321L));

        // 2. IsParentAgeValid (parent must have been created at or before child)
        Assert.True(WindowFocuser.IsParentAgeValid(1000L, 2000L));
        Assert.True(WindowFocuser.IsParentAgeValid(1000L, 1000L));
        Assert.False(WindowFocuser.IsParentAgeValid(2000L, 1000L)); // Recycled parent created after child!

        // 3. VerifyHwndOwnership with mock delegates
        var mockHwnd = new IntPtr(0x1234);
        const int expectedPid = 4242;
        const long expectedTicks = 555555L;

        // Valid match
        bool valid = WindowFocuser.VerifyHwndOwnership(
            mockHwnd,
            expectedPid,
            expectedTicks,
            getStartTime: pid => expectedTicks,
            getOwnerPid: hwnd => (uint)expectedPid,
            isWindowValid: hwnd => true);
        Assert.True(valid);

        // PID mismatch
        bool pidMismatch = WindowFocuser.VerifyHwndOwnership(
            mockHwnd,
            expectedPid,
            expectedTicks,
            getStartTime: pid => expectedTicks,
            getOwnerPid: hwnd => 9999,
            isWindowValid: hwnd => true);
        Assert.False(pidMismatch);

        // Start ticks mismatch (recycled PID)
        bool ticksMismatch = WindowFocuser.VerifyHwndOwnership(
            mockHwnd,
            expectedPid,
            expectedTicks,
            getStartTime: pid => 888888L,
            getOwnerPid: hwnd => (uint)expectedPid,
            isWindowValid: hwnd => true);
        Assert.False(ticksMismatch);

        // Invalid window
        bool invalidWindow = WindowFocuser.VerifyHwndOwnership(
            mockHwnd,
            expectedPid,
            expectedTicks,
            getStartTime: pid => expectedTicks,
            getOwnerPid: hwnd => (uint)expectedPid,
            isWindowValid: hwnd => false);
        Assert.False(invalidWindow);
    }

    [Fact]
    public void WindowFocuser_AncestryValidation_GuardsAgainstPidRecyclingAndCycles()
    {
        var parentMap = new Dictionary<int, int>
        {
            [100] = 200,
            [200] = 300,
            [300] = 100 // cycle
        };

        var hwnd200 = new IntPtr(0x200);
        var hwnd300 = new IntPtr(0x300);

        // 1. PID recycled immediately after ancestry snapshot -> aborts!
        int callCount100 = 0;
        bool focusedRecycledStart = WindowFocuser.TryFocusProcessWindowCore(
            pid: 100,
            expectedStartTicks: 1000L,
            getStartTime: p =>
            {
                if (p == 100)
                {
                    callCount100++;
                    return callCount100 == 1 ? 1000L : 9999L; // recycled after snapshot!
                }
                return 500L;
            },
            getAncestry: () => parentMap,
            findWindow: p => p == 200 ? hwnd200 : IntPtr.Zero,
            verifyOwnership: (h, p, t) => true,
            focusWindow: h => true);
        Assert.False(focusedRecycledStart);

        // 2. Child PID recycled during hop child -> parent -> aborts!
        int childQueryCount = 0;
        bool focusedRecycledChild = WindowFocuser.TryFocusProcessWindowCore(
            pid: 100,
            expectedStartTicks: 1000L,
            getStartTime: p =>
            {
                if (p == 100)
                {
                    childQueryCount++;
                    // 1st: initial check, 2nd: post-snapshot check, 3rd: hop child verification -> recycled!
                    return childQueryCount < 3 ? 1000L : 8888L;
                }
                return 500L;
            },
            getAncestry: () => parentMap,
            findWindow: p => p == 200 ? hwnd200 : IntPtr.Zero,
            verifyOwnership: (h, p, t) => true,
            focusWindow: h => true);
        Assert.False(focusedRecycledChild);

        // 3. Parent created AFTER child (recycled parent PID) -> aborts!
        bool focusedInvalidParentAge = WindowFocuser.TryFocusProcessWindowCore(
            pid: 100,
            expectedStartTicks: 1000L,
            getStartTime: p => p switch
            {
                100 => 1000L,
                200 => 2000L, // Parent created AFTER child (2000 > 1000)
                _ => null
            },
            getAncestry: () => parentMap,
            findWindow: p => p == 200 ? hwnd200 : IntPtr.Zero,
            verifyOwnership: (h, p, t) => true,
            focusWindow: h => true);
        Assert.False(focusedInvalidParentAge);

        // 4. Cycle in ancestry -> terminates gracefully without infinite loop
        bool cycleTerminated = WindowFocuser.TryFocusProcessWindowCore(
            pid: 100,
            expectedStartTicks: 1000L,
            getStartTime: p => p switch
            {
                100 => 1000L,
                200 => 800L,
                300 => 600L,
                _ => null
            },
            getAncestry: () => parentMap,
            findWindow: p => IntPtr.Zero, // no windows anywhere
            verifyOwnership: (h, p, t) => false,
            focusWindow: h => false);
        Assert.False(cycleTerminated);

        // 5. Valid ancestry walk finds ancestor window and focuses it
        IntPtr focusedHwnd = IntPtr.Zero;
        bool focusSuccess = WindowFocuser.TryFocusProcessWindowCore(
            pid: 100,
            expectedStartTicks: 1000L,
            getStartTime: p => p switch
            {
                100 => 1000L,
                200 => 800L,
                _ => null
            },
            getAncestry: () => parentMap,
            findWindow: p => p == 200 ? hwnd200 : IntPtr.Zero,
            verifyOwnership: (h, p, t) => h == hwnd200 && p == 200 && t == 800L,
            focusWindow: h => { focusedHwnd = h; return true; });
        Assert.True(focusSuccess);
        Assert.Equal(hwnd200, focusedHwnd);
    }

    [Fact]
    public void AgentDetailToast_SingleInstanceReplacement_RaceSafety()
    {
        var snap1 = new AgentSnapshot("t1", "Agent One", AgentRole.Leader, "kakashi", AgentState.Working);
        var snap2 = new AgentSnapshot("t2", "Agent Two", AgentRole.Coder, "minato", AgentState.Working);

        AgentDetailToast.ShowToast(snap1);
        var toast1 = AgentDetailToast.ActiveToast;
        Assert.NotNull(toast1);

        AgentDetailToast.ShowToast(snap2);
        var toast2 = AgentDetailToast.ActiveToast;
        Assert.NotNull(toast2);
        Assert.NotSame(toast1, toast2);

        // Disposing or closing toast1 should NEVER clear toast2 from ActiveToast
        toast1.Close();
        Assert.Same(toast2, AgentDetailToast.ActiveToast);

        // Only closing toast2 clears ActiveToast
        toast2.Close();
        Assert.Null(AgentDetailToast.ActiveToast);
    }

    [Fact]
    public void PetOverlayForm_FocusAgentAction_DispatchesToProcessWindowOrToast()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        using var form = new PetOverlayForm(theme, initialScale: 1);

        var procAgent = new AgentSnapshot(
            Id: "proc:test:abc123",
            Name: "Test Proc",
            Role: AgentRole.Coder,
            SpriteKey: "minato",
            State: AgentState.Working,
            ProcessId: 99999999, // Non-existent PID to test toast fallback
            ProcessStartTicks: 12345L,
            Source: "process"
        );

        var state = new HqState(
            Agents: new List<AgentSnapshot> { procAgent },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: Array.Empty<HqEvent>()
        );

        form.ApplyRenderState(state, theme, petPaused: false, petHiddenByDndOrLock: false);

        // In single process working mode, the working pill emits focus_agent:proc:test:abc123
        // Clicking on working pill coordinates (X=346, Y=75) triggers focus_agent
        form.SimulateMouseDown(346, 75, MouseButtons.Left);

        // Since PID 99999999 doesn't exist, WindowFocuser returns false and falls back to AgentDetailToast
        var toast = AgentDetailToast.ActiveToast;
        Assert.NotNull(toast);
        Assert.Equal("proc:test:abc123", toast.Agent.Id);
        toast.Close();
    }

    [Fact]
    public void ShowToast_WithWorkStartedAt_RendersElapsedWithoutException()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = new AgentSnapshot(
            "ag-timer",
            "Minato",
            AgentRole.Coder,
            "minato",
            AgentState.Working,
            WorkStartedAt: now.AddSeconds(-102)
        );

        using var toast = new AgentDetailToast(agent);
        Assert.NotNull(toast.Agent.WorkStartedAt);
        _ = toast.Handle;
        toast.Refresh();
        toast.Close();
    }

    [Fact]
    public void FormatStateRoleLine_AllAgentTypesIncludingMinatoWorkerAndClones_NeverDisplaysRoleClone()
    {
        var now = new DateTimeOffset(2026, 11, 26, 1, 45, 0, TimeSpan.FromHours(7));

        var cases = new[]
        {
            // Regression: Minato worker even if mistakenly tagged AgentRole.Clone MUST show Role: Coder
            (new AgentSnapshot("wt:minato-v21", "Minato \u00B7 KageDesk v2.1 (Mixi + Variety)", AgentRole.Clone, "clone", AgentState.Working, Repo: "kagedesk", Source: "orca", WorkStartedAt: now.AddSeconds(-95)), "Coder"),
            (new AgentSnapshot("wt:shika", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Working, Repo: "Bots", Source: "orca"), "Leader"),
            (new AgentSnapshot("wt:kakashi", "Kakashi \u00B7 PR Review", AgentRole.Reviewer, "kakashi", AgentState.Working, Repo: "kagedesk", Source: "orca"), "Reviewer"),
            (new AgentSnapshot("wt:tsunade", "Tsunade \u00B7 Final QA", AgentRole.Tester, "tsunade", AgentState.Working, Repo: "kagedesk", Source: "orca"), "Tester"),
            // Non-canonical Orca worker with AgentRole.Clone -> MUST show Worker, NEVER Clone
            (new AgentSnapshot("wt:generic", "Worker #1", AgentRole.Clone, "mixi", AgentState.Working, Repo: "kagedesk", Source: "orca"), "Worker"),
            // Standalone CLI sessions -> MUST show CLI Session, NEVER Clone
            (new AgentSnapshot("proc:omp:1001", "OMP", AgentRole.Clone, "mixi", AgentState.Idle, ProcessId: 6660, Source: "process"), "CLI Session"),
            (new AgentSnapshot("proc:claude:1002", "Claude", AgentRole.External, "neko-dev", AgentState.Working, ProcessId: 7770, Source: "process"), "CLI Session"),
            // Generic external hook -> Session
            (new AgentSnapshot("hook:ext:1", "Agent", AgentRole.External, "wizard-dev", AgentState.Working, Source: "bridge"), "Session")
        };

        foreach (var (agent, expectedRoleLabel) in cases)
        {
            string roleLabel = AgentDetailToast.FormatRoleLabel(agent);
            string line = AgentDetailToast.FormatStateRoleLine(agent, now);

            Assert.Equal(expectedRoleLabel, roleLabel);
            Assert.Contains($"Role: {expectedRoleLabel}", line);
            Assert.DoesNotContain("Role: Clone", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Clone", roleLabel, StringComparison.OrdinalIgnoreCase);
        }
    }
}

