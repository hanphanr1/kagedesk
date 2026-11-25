using KageDesk.App.Services;
using KageDesk.Core.Config;
using KageDesk.Core.Models;
using Xunit;

namespace KageDesk.App.Tests;

[Collection("WinFormsGdi")]
public class AudioMuteAndRevisionTrackingTests : IDisposable
{
    private readonly string _tempDir;

    public AudioMuteAndRevisionTrackingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-audio-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private ConfigStore CreateIsolatedConfigStore(Action<AppConfig>? configure = null)
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        var store = new ConfigStore(configPath);
        var cfg = store.Load();
        cfg.SoundEnabled = true;
        cfg.HidePetsInFullscreenGame = false;
        configure?.Invoke(cfg);
        store.Save(cfg);
        return store;
    }

    [Fact]
    public void UpdateEffectiveAudioMute_TogglingDndWhileFullscreenTrue_UpdatesMuteImmediatelyBothWays()
    {
        var store = CreateIsolatedConfigStore();
        using var runtime = new KageDeskRuntime(store, noWallpaper: true);

        // Initially sound is enabled, not fullscreen, not locked -> IsMuted = false
        Assert.False(runtime.SoundEngine.IsMuted);

        // Set fullscreen game active, but DND config is false -> IsMuted remains false
        runtime.OcclusionMonitor.SetTestOcclusionState(isFullscreenGame: true, isSessionLocked: false);
        Assert.False(runtime.SoundEngine.IsMuted);

        // 1. Toggle DND ON while fullscreen stays true -> IsMuted becomes true immediately
        runtime.UpdateConfig(c => c.HidePetsInFullscreenGame = true);
        Assert.True(runtime.SoundEngine.IsMuted);

        // 2. Toggle DND OFF while fullscreen stays true -> IsMuted becomes false immediately
        runtime.UpdateConfig(c => c.HidePetsInFullscreenGame = false);
        Assert.False(runtime.SoundEngine.IsMuted);

        // 3. Session locked -> IsMuted becomes true immediately
        runtime.OcclusionMonitor.SetTestOcclusionState(isSessionLocked: true);
        Assert.True(runtime.SoundEngine.IsMuted);

        // Unlock session -> IsMuted returns to false
        runtime.OcclusionMonitor.SetTestOcclusionState(isSessionLocked: false);
        Assert.False(runtime.SoundEngine.IsMuted);

        // 4. SoundEnabled toggled off -> IsMuted becomes true immediately
        runtime.UpdateConfig(c => c.SoundEnabled = false);
        Assert.True(runtime.SoundEngine.IsMuted);

        // SoundEnabled toggled back on -> IsMuted returns to false
        runtime.UpdateConfig(c => c.SoundEnabled = true);
        Assert.False(runtime.SoundEngine.IsMuted);
    }

    [Fact]
    public void OnStateChangedForAudio_StaleRevision_DoesNotReplayCuesOrRollBackAgentStates()
    {
        var store = CreateIsolatedConfigStore();
        using var runtime = new KageDeskRuntime(store, noWallpaper: true);

        var agent10 = new AgentSnapshot("coder-1", "Minato", AgentRole.Coder, "minato", AgentState.Working);
        var state10 = new HqState(
            Agents: new[] { agent10 },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Revision: 10
        );

        // Process revision 10
        runtime.OnStateChangedForAudio(state10);
        Assert.Equal(AgentState.Working, runtime.GetLastAgentState("coder-1"));

        // Agent advances to Done in revision 11
        var agent11 = agent10 with { State = AgentState.Done };
        var state11 = state10 with
        {
            Agents = new[] { agent11 },
            Mood = HqMood.Celebrating,
            Revision = 11
        };
        runtime.OnStateChangedForAudio(state11);
        Assert.Equal(AgentState.Done, runtime.GetLastAgentState("coder-1"));

        // Out-of-order stale background notification arriving with Revision 10 (or 9) with older state Working
        var staleState = state10 with
        {
            Agents = new[] { agent10 with { State = AgentState.NeedsApproval } },
            Revision = 10
        };
        runtime.OnStateChangedForAudio(staleState);

        // Must NOT roll back _lastAgentStates to NeedsApproval or Working
        Assert.Equal(AgentState.Done, runtime.GetLastAgentState("coder-1"));

        // Same revision (11) must also be ignored
        var duplicateState = state11 with
        {
            Agents = new[] { agent10 with { State = AgentState.Failed } },
            Revision = 11
        };
        runtime.OnStateChangedForAudio(duplicateState);
        Assert.Equal(AgentState.Done, runtime.GetLastAgentState("coder-1"));
    }

    [Theory]
    [InlineData(true, false, false, false, false)] // all normal -> unmuted
    [InlineData(false, false, false, false, true)]  // sound disabled -> muted
    [InlineData(true, true, false, false, true)]   // session locked -> muted
    [InlineData(true, false, true, true, true)]    // fullscreen + DND on -> muted
    [InlineData(true, false, true, false, false)]  // fullscreen + DND off -> unmuted
    [InlineData(true, false, false, true, false)]  // not fullscreen + DND on -> unmuted
    public void UpdateEffectiveAudioMute_MuteMatrixCombinations(
        bool soundEnabled,
        bool sessionLocked,
        bool fullscreenGame,
        bool hidePetsInGame,
        bool expectedMuted)
    {
        var store = CreateIsolatedConfigStore(c =>
        {
            c.SoundEnabled = soundEnabled;
            c.HidePetsInFullscreenGame = hidePetsInGame;
        });
        using var runtime = new KageDeskRuntime(store, noWallpaper: true);
        runtime.OcclusionMonitor.SetTestOcclusionState(isFullscreenGame: fullscreenGame, isSessionLocked: sessionLocked);

        Assert.Equal(expectedMuted, runtime.SoundEngine.IsMuted);
    }

    [Fact]
    public void OnStateChangedForAudio_ZeroRevision_ProcessesEventsNormally()
    {
        var store = CreateIsolatedConfigStore();
        using var runtime = new KageDeskRuntime(store, noWallpaper: true);

        var agent = new AgentSnapshot("coder-legacy", "Minato", AgentRole.Coder, "minato", AgentState.Working);
        var state = new HqState(
            Agents: new[] { agent },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Revision: 0 // Legacy/unrevisioned state
        );

        runtime.OnStateChangedForAudio(state);
        Assert.Equal(AgentState.Working, runtime.GetLastAgentState("coder-legacy"));
    }
}

