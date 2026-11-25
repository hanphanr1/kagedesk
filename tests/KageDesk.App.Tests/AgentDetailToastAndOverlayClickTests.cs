using System.Drawing;
using System.Windows.Forms;
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
}
