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
}

