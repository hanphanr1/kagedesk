using System.Diagnostics;
using KageDesk.Core.Models;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;
using KageDesk.Rendering.Sprites;
using Xunit;
using Xunit.Abstractions;

namespace KageDesk.Rendering.Tests;

public class V22InteractiveDramaAndParityTests
{
    private readonly ITestOutputHelper _output;

    public V22InteractiveDramaAndParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static HqState CreateIdleState(
        EffectiveWeather weather = EffectiveWeather.Day,
        IReadOnlyList<HqEvent>? visualEvents = null) =>
        new(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: weather,
            Mood: HqMood.Calm,
            PendingVisualEvents: visualEvents ?? Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);

    private static HqState CreateSquadState(
        EffectiveWeather weather = EffectiveWeather.Day,
        IReadOnlyList<HqEvent>? visualEvents = null,
        AgentState stateOverride = AgentState.Working)
    {
        var agents = new[]
        {
            new AgentSnapshot("shikamaru", "Shikamaru", AgentRole.Leader, "shikamaru", stateOverride, "Planning", "READ"),
            new AgentSnapshot("minato", "Minato", AgentRole.Coder, "minato", stateOverride, "Coding feature", "CODE"),
            new AgentSnapshot("tsunade", "Tsunade", AgentRole.Tester, "tsunade", stateOverride, "Running tests", "TEST"),
            new AgentSnapshot("kakashi", "Kakashi", AgentRole.Reviewer, "kakashi", stateOverride, "Reviewing PR", "READ")
        };

        return new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: weather,
            Mood: stateOverride == AgentState.Idle ? HqMood.Calm : HqMood.Busy,
            PendingVisualEvents: visualEvents ?? Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static long ComputeBufferChecksum(PixelBuffer buf)
    {
        var span = buf.ReadOnlyPixels;
        long sum = 0;
        for (int i = 0; i < span.Length; i++)
        {
            sum = unchecked(sum * 31 + span[i]);
        }
        return sum;
    }

    [Fact]
    public void MixiPoke_SpeechBubbleAndStartledBounce_RenderIdenticallyOnWallpaperAndTaskbarPet()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        var idle = CreateIdleState(EffectiveWeather.MidnightRain);

        var wp = new HqWallpaperRenderer();
        var pet = new TaskbarPetRenderer();
        using var wpBase = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var wpPoke = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petBase = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        using var petPoke = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        wp.Render(wpBase, idle, theme, 2.0f, showMixiWhenAllIdle: true, effects: default);
        pet.Render(petBase, idle, theme, 2.0f, showIdleChill: true, showMixiWhenAllIdle: true, effects: default);

        Assert.True(wp.IsMixiChillActive);
        Assert.NotNull(wp.MixiHitBounds);
        Assert.True(pet.IsMixiChillActive);

        // Verify ALL 6 Vietnamese streamer lines fold & render cleanly without clipping/throwing on both renderers
        foreach (string line in MixiChillRenderer.PokeLines)
        {
            var pokeEffects = new SceneEffectsState(
                PinnedMixiActivity: MixiChillActivity.EatingNoodles,
                MixiSpeechText: line,
                MixiPokeElapsedSeconds: 0.20f,
                IsMediaPlaying: false);

            wp.Render(wpPoke, idle, theme, 2.0f, showMixiWhenAllIdle: true, effects: pokeEffects);
            pet.Render(petPoke, idle, theme, 2.0f, showIdleChill: true, showMixiWhenAllIdle: true, effects: pokeEffects);

            Assert.NotEqual(ComputeBufferChecksum(wpBase), ComputeBufferChecksum(wpPoke));
            Assert.NotEqual(ComputeBufferChecksum(petBase), ComputeBufferChecksum(petPoke));
            Assert.Equal(MixiChillActivity.EatingNoodles, wp.ActiveMixiActivity);
            Assert.Equal(MixiChillActivity.EatingNoodles, pet.ActiveMixiActivity);
        }
    }

    [Theory]
    [InlineData(MixiChillActivity.EatingNoodles, "noodles")]
    [InlineData(MixiChillActivity.WatchingYouTube, "youtube")]
    [InlineData(MixiChillActivity.Gaming, "gaming")]
    [InlineData(MixiChillActivity.StreamingTalk, "stream")]
    [InlineData(MixiChillActivity.SingingKaraoke, "karaoke")]
    public void MixiPinnedActivity_RoundTripsConfigAndPinsBothRenderers(MixiChillActivity activity, string expectedConfig)
    {
        Assert.Equal(expectedConfig, MixiChillRenderer.ToConfigValue(activity));
        Assert.Equal(activity, MixiChillRenderer.ParsePinnedActivity(expectedConfig));
        Assert.Null(MixiChillRenderer.ParsePinnedActivity("auto"));

        var theme = ThemePackLoader.BuiltInFallback;
        var idle = CreateIdleState();
        var effects = new SceneEffectsState(PinnedMixiActivity: activity);

        var wp = new HqWallpaperRenderer();
        var pet = new TaskbarPetRenderer();
        using var wpBuf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petBuf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        wp.Render(wpBuf, idle, theme, 12.0f, showMixiWhenAllIdle: true, effects: effects);
        pet.Render(petBuf, idle, theme, 12.0f, showIdleChill: true, showMixiWhenAllIdle: true, effects: effects);

        Assert.Equal(activity, wp.ActiveMixiActivity);
        Assert.Equal(activity, pet.ActiveMixiActivity);
    }

    [Fact]
    public void TeamDrama_ReviewChangesRedScrollAndPanicSprint_SharedTimelineParityAcrossWallpaperAndTaskbarPet()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        var dramaEv = new HqEvent(10, "review_changes", "REQUEST_CHANGES", DateTimeOffset.UtcNow, AgentId: "kakashi", TargetAgentId: "minato");
        var state = CreateSquadState(EffectiveWeather.Day, new[] { dramaEv });

        var sharedDrama = new TeamDramaController();
        var wp = new HqWallpaperRenderer(sharedDrama);
        var pet = new TaskbarPetRenderer(sharedDrama);
        using var wpBuf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petBuf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        // t=0.05s: red scroll flight begins cleanly on shared timeline
        wp.Render(wpBuf, state, theme, 0.05f);
        pet.Render(petBuf, state, theme, 0.05f);
        Assert.True(wp.IsDramaActive);
        Assert.True(pet.IsDramaActive);
        Assert.True(sharedDrama.IsRedScrollFlying);

        // Advance 0.50s (must STILL be flying — proves shared controller does NOT double-tick dt!)
        for (int step = 1; step <= 10; step++)
        {
            float t = 0.05f + step * 0.05f; // up to 0.55s (< 0.90s ScrollFlightDuration)
            wp.Render(wpBuf, state, theme, t);
            pet.Render(petBuf, state, theme, t);
        }
        Assert.True(sharedDrama.IsRedScrollFlying);
        Assert.False(sharedDrama.IsPanicSprintActive);

        // Advance past scroll flight (0.90s) into 6s Panic Sprint
        for (int step = 11; step <= 25; step++)
        {
            float t = 0.05f + step * 0.05f; // up to 1.30s
            wp.Render(wpBuf, state, theme, t);
            pet.Render(petBuf, state, theme, t);
        }

        Assert.True(wp.IsDramaActive);
        Assert.True(pet.IsDramaActive);
        Assert.True(sharedDrama.IsPanicSprintActive);
        Assert.Equal("minato", sharedDrama.BoundTargetAgentId);
        Assert.True(sharedDrama.IsSlotInPanicSprint("minato", "minato"));
        Assert.False(sharedDrama.IsSlotInPanicSprint("kakashi", "kakashi"));

        // Advance past 6s Panic Sprint -> cleanly expires on both simultaneously
        for (int step = 0; step < 50; step++)
        {
            float t = 1.35f + step * 0.15f;
            wp.Render(wpBuf, state, theme, t);
            pet.Render(petBuf, state, theme, t);
        }

        Assert.False(wp.IsDramaActive);
        Assert.False(pet.IsDramaActive);
    }

    [Fact]
    public void TeamDrama_SingleBoundTarget_NoDualActorPanicSprint_AndFailedLookupPreservesActiveDrama()
    {
        var controller = new TeamDramaController();
        var particles = new ParticleSystem();
        var slots = new[]
        {
            new SlotBinding(0, "kakashi-1", "kakashi", AgentState.Working, true, 328, 118),
            new SlotBinding(1, "minato-a", "minato", AgentState.Working, true, 260, 118),
            new SlotBinding(2, "minato-b", "minato", AgentState.Working, true, 192, 118)
        };

        // Trigger drama targeting concrete "minato-a"
        var validDrama = new HqEvent(1, "review_changes", "FIX", DateTimeOffset.UtcNow, AgentId: "kakashi-1", TargetAgentId: "minato-a", FromIsConcreteId: true, ToIsConcreteId: true);
        var state1 = CreateSquadState(visualEvents: new[] { validDrama });

        controller.Update(0.05f, 0.05f, state1, slots, particles, 384, 12, 374, 16, emitTick: false);
        Assert.True(controller.IsRedScrollFlying);
        Assert.Equal("minato-a", controller.BoundTargetAgentId);

        // Now send a failing drama targeting missing concrete ID "missing-actor": MUST NOT overwrite active state!
        var invalidDrama = new HqEvent(2, "review_changes", "BAD", DateTimeOffset.UtcNow, AgentId: "kakashi-1", TargetAgentId: "missing-actor", FromIsConcreteId: true, ToIsConcreteId: true);
        var state2 = CreateSquadState(visualEvents: new[] { validDrama, invalidDrama });

        // Advance past scroll flight into Panic Sprint
        for (int i = 1; i <= 12; i++)
        {
            controller.Update(0.10f, 0.05f + i * 0.10f, state2, slots, particles, 384, 12, 374, 16, emitTick: false);
        }

        Assert.True(controller.IsPanicSprintActive);
        Assert.Equal("minato-a", controller.BoundTargetAgentId);
        // ONLY the single concrete bound actor sprints — NOT minato-b even though both share sprite "minato"!
        Assert.True(controller.IsSlotInPanicSprint("minato-a", "minato"));
        Assert.False(controller.IsSlotInPanicSprint("minato-b", "minato"));
        Assert.False(controller.IsSlotInPanicSprint("kakashi-1", "kakashi"));
    }

    [Fact]
    public void TaskbarPet_ApprovalModeAdvancesDramaTimersWithoutConsumingUnseenDramaEvents()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        var pet = new TaskbarPetRenderer();
        using var petBuf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var dramaEv = new HqEvent(50, "review_changes", "FIX PR", DateTimeOffset.UtcNow, AgentId: "kakashi", TargetAgentId: "minato");
        var approvalAgents = new[]
        {
            new AgentSnapshot("shikamaru", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.NeedsApproval, "Approve?", "WAIT"),
            new AgentSnapshot("minato", "Minato", AgentRole.Coder, "minato", AgentState.Working, "Coding", "CODE"),
            new AgentSnapshot("kakashi", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, "Review", "READ")
        };
        var approvalState = new HqState(approvalAgents, Array.Empty<AccountInfo>(), Array.Empty<BuildInfo>(), EffectiveWeather.Day, HqMood.Alert, new[] { dramaEv }, DemoScenario: null, Timestamp: DateTimeOffset.UtcNow);

        // Render during approval mode: pending drama event MUST NOT be consumed!
        pet.Render(petBuf, approvalState, theme, 0.10f);
        Assert.False(pet.IsDramaActive);
        Assert.True(pet.DramaController.LastHandledDramaSeq < 50);

        // Approval clears -> unseen drama plays immediately!
        var clearState = CreateSquadState(visualEvents: new[] { dramaEv });
        pet.Render(petBuf, clearState, theme, 0.20f);
        Assert.True(pet.IsDramaActive);
        Assert.Equal(50, pet.DramaController.LastHandledDramaSeq);
    }

    [Fact]
    public void SquadCelebration_ExcludesMixiOnBothRenderers_AndTwoDoneStopsHoppingAfterExpiry()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        var celebrateEv = new HqEvent(25, "celebrate", "CI Passed!", DateTimeOffset.UtcNow);
        var idleWithCelebrate = CreateSquadState(EffectiveWeather.Day, new[] { celebrateEv }, stateOverride: AgentState.Idle);

        var sharedDrama = new TeamDramaController();
        var wp = new HqWallpaperRenderer(sharedDrama);
        var pet = new TaskbarPetRenderer(sharedDrama);
        using var wpBuf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petBuf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        // Active celebration MUST exclude Mixi on BOTH Wallpaper and Taskbar Pet!
        wp.Render(wpBuf, idleWithCelebrate, theme, 0.10f, showMixiWhenAllIdle: true);
        pet.Render(petBuf, idleWithCelebrate, theme, 0.10f, showIdleChill: true, showMixiWhenAllIdle: true);
        Assert.True(wp.IsCelebrationActive);
        Assert.True(pet.IsCelebrationActive);
        Assert.False(wp.IsMixiChillActive);
        Assert.False(pet.IsMixiChillActive);

        // Advance past 3s celebration window -> expires cleanly AND Mixi reappears even if consumed seq 25 is still in PendingVisualEvents!
        for (int step = 1; step <= 25; step++)
        {
            float t = 0.10f + step * 0.15f;
            wp.Render(wpBuf, idleWithCelebrate, theme, t, showMixiWhenAllIdle: true);
            pet.Render(petBuf, idleWithCelebrate, theme, t, showIdleChill: true, showMixiWhenAllIdle: true);
        }

        Assert.False(wp.IsCelebrationActive);
        Assert.False(pet.IsCelebrationActive);
        Assert.True(wp.IsMixiChillActive);
        Assert.True(pet.IsMixiChillActive);

        // 2-Done branch: transitions to bounded 3s celebration and STOPS hopping when timer expires!
        var twoDonePet = new TaskbarPetRenderer();
        var twoDoneState = CreateSquadState(EffectiveWeather.Day, stateOverride: AgentState.Done);
        twoDonePet.Render(petBuf, twoDoneState, theme, 0.10f);
        Assert.True(twoDonePet.IsCelebrationActive);

        for (int step = 1; step <= 25; step++)
        {
            twoDonePet.Render(petBuf, twoDoneState, theme, 0.10f + step * 0.15f);
        }
        Assert.False(twoDonePet.IsCelebrationActive);
    }

    [Fact]
    public void NightWarmLampsAndMediaEqualizer_RenderDistinctlyOnBothWallpaperAndTaskbarPet()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        var dayState = CreateSquadState(EffectiveWeather.Day);
        var nightState = CreateSquadState(EffectiveWeather.MidnightRain);

        var wp = new HqWallpaperRenderer();
        var pet = new TaskbarPetRenderer();
        using var wpDay = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var wpNightMusic = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petDay = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        using var petNightMusic = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        wp.Render(wpDay, dayState, theme, 1.5f, effects: default);
        pet.Render(petDay, dayState, theme, 1.5f, effects: default);

        var mediaEffects = new SceneEffectsState(IsMediaPlaying: true);
        wp.Render(wpNightMusic, nightState, theme, 1.5f, effects: mediaEffects);
        pet.Render(petNightMusic, nightState, theme, 1.5f, effects: mediaEffects);

        Assert.NotEqual(ComputeBufferChecksum(wpDay), ComputeBufferChecksum(wpNightMusic));
        Assert.NotEqual(ComputeBufferChecksum(petDay), ComputeBufferChecksum(petNightMusic));
    }

    [Fact]
    public void HotPath_ZeroBytesPerFrameSteadyState_AndBoundedRenderTime_AcrossAllV22Modes()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        var wp = new HqWallpaperRenderer();
        var pet = new TaskbarPetRenderer();
        using var wpBuf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petBuf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var nightWorking = CreateSquadState(EffectiveWeather.MidnightRain);
        var idleMixi = CreateIdleState(EffectiveWeather.MidnightRain);
        var dramaState = CreateSquadState(EffectiveWeather.Day, new[]
        {
            new HqEvent(100, "review_changes", "REQUEST_CHANGES", DateTimeOffset.UtcNow, AgentId: "kakashi", TargetAgentId: "minato")
        });
        var celebrateState = CreateSquadState(EffectiveWeather.Day, new[]
        {
            new HqEvent(101, "celebrate", "Merged!", DateTimeOffset.UtcNow)
        });

        var mixiPokeEffects = new SceneEffectsState(
            PinnedMixiActivity: MixiChillActivity.SingingKaraoke,
            MixiSpeechText: "ĐANG LÀM BÁT MÌ!",
            MixiPokeElapsedSeconds: 0.4f,
            IsMediaPlaying: true);
        var mediaEffects = new SceneEffectsState(IsMediaPlaying: true, WakeUpPulseRemainingSeconds: 1.5f);

        // Steady-state hot-path measurement (post-warmup; does NOT claim transition allocations or GDI soak)
        (string Label, HqState State, SceneEffectsState Effects)[] scenarios =
        {
            ("NightWorking+Media", nightWorking, mediaEffects),
            ("MixiPoke+Night+Media", idleMixi, mixiPokeEffects),
            ("ReviewDrama+Panic", dramaState, mediaEffects),
            ("SquadCelebration", celebrateState, mediaEffects)
        };

        foreach (var sc in scenarios)
        {
            wp.ResetEffects();
            pet.ResetEffects();

            // Warmup 35 frames so state, caches, and Tier-1 JIT settle
            for (int i = 0; i < 35; i++)
            {
                float t = i * 0.05f;
                wp.Render(wpBuf, sc.State, theme, t, showMixiWhenAllIdle: true, effects: sc.Effects);
                pet.Render(petBuf, sc.State, theme, t, showIdleChill: true, showMixiWhenAllIdle: true, effects: sc.Effects);
            }

            const int frames = 40;
            var swWp = new Stopwatch();
            var swPet = new Stopwatch();

            long beforeWp = GC.GetAllocatedBytesForCurrentThread();
            swWp.Restart();
            for (int i = 0; i < frames; i++)
            {
                wp.Render(wpBuf, sc.State, theme, 1.80f + i * 0.033f, showMixiWhenAllIdle: true, effects: sc.Effects);
            }
            swWp.Stop();
            long allocWp = GC.GetAllocatedBytesForCurrentThread() - beforeWp;

            long beforePet = GC.GetAllocatedBytesForCurrentThread();
            swPet.Restart();
            for (int i = 0; i < frames; i++)
            {
                pet.Render(petBuf, sc.State, theme, 1.80f + i * 0.033f, showIdleChill: true, showMixiWhenAllIdle: true, effects: sc.Effects);
            }
            swPet.Stop();
            long allocPet = GC.GetAllocatedBytesForCurrentThread() - beforePet;

            double wpAvgMs = swWp.Elapsed.TotalMilliseconds / frames;
            double petAvgMs = swPet.Elapsed.TotalMilliseconds / frames;
            _output.WriteLine($"[{sc.Label}] WP: {wpAvgMs:F3} ms/f ({allocWp} B steady-state), Pet: {petAvgMs:F3} ms/f ({allocPet} B steady-state)");

            Assert.Equal(0, allocWp);
            Assert.Equal(0, allocPet);
            Assert.True(wpAvgMs < 15.0, $"Expected steady-state Wallpaper render < 15.0 ms/f for {sc.Label}, got {wpAvgMs:F3} ms/f.");
            Assert.True(petAvgMs < 8.0, $"Expected steady-state TaskbarPet render < 8.0 ms/f for {sc.Label}, got {petAvgMs:F3} ms/f.");
        }
    }
}
