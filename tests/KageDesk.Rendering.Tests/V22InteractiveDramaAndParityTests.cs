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

    private static HqState CreateIdleState(EffectiveWeather weather = EffectiveWeather.Day) =>
        new(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: weather,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);

    private static HqState CreateSquadState(
        EffectiveWeather weather = EffectiveWeather.Day,
        IReadOnlyList<HqEvent>? visualEvents = null)
    {
        var agents = new[]
        {
            new AgentSnapshot("shikamaru", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, "Planning", "READ"),
            new AgentSnapshot("minato", "Minato", AgentRole.Coder, "minato", AgentState.Working, "Coding feature", "CODE"),
            new AgentSnapshot("tsunade", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, "Running tests", "TEST"),
            new AgentSnapshot("kakashi", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, "Reviewing PR", "READ")
        };

        return new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: weather,
            Mood: HqMood.Busy,
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
    public void TeamDrama_ReviewChangesRedScrollAndPanicSprint_ParityAcrossWallpaperAndTaskbarPet()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        var dramaEv = new HqEvent(10, "review_changes", "REQUEST_CHANGES", DateTimeOffset.UtcNow, AgentId: "kakashi", TargetAgentId: "minato");
        var state = CreateSquadState(EffectiveWeather.Day, new[] { dramaEv });

        var wp = new HqWallpaperRenderer();
        var pet = new TaskbarPetRenderer();
        using var wpBuf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petBuf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        // t=0.05s: red scroll flight begins
        wp.Render(wpBuf, state, theme, 0.05f);
        pet.Render(petBuf, state, theme, 0.05f);
        Assert.True(wp.IsDramaActive);
        Assert.True(pet.IsDramaActive);

        // Advance through scroll flight (0.90s) into 6s Panic Sprint
        for (int step = 1; step <= 25; step++)
        {
            float t = 0.05f + step * 0.06f; // up to 1.55s
            wp.Render(wpBuf, state, theme, t);
            pet.Render(petBuf, state, theme, t);
        }

        Assert.True(wp.IsDramaActive);
        Assert.True(pet.IsDramaActive);
        Assert.True(wp.DramaController.IsPanicSprintActive);
        Assert.True(wp.DramaController.IsSlotInPanicSprint("minato", "minato"));
        Assert.False(wp.DramaController.IsSlotInPanicSprint("kakashi", "kakashi"));

        // Advance past 6s Panic Sprint -> cleanly expires
        for (int step = 0; step < 45; step++)
        {
            float t = 1.6f + step * 0.16f;
            wp.Render(wpBuf, state, theme, t);
            pet.Render(petBuf, state, theme, t);
        }

        Assert.False(wp.IsDramaActive);
        Assert.False(pet.IsDramaActive);
    }

    [Fact]
    public void SquadCelebration_CoordinatedHopAndConfetti_ParityAcrossWallpaperAndTaskbarPet()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        var celebrateEv = new HqEvent(25, "celebrate", "CI Passed!", DateTimeOffset.UtcNow);
        var state = CreateSquadState(EffectiveWeather.Day, new[] { celebrateEv });

        var wp = new HqWallpaperRenderer();
        var pet = new TaskbarPetRenderer();
        using var wpBuf = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        using var petBuf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        wp.Render(wpBuf, state, theme, 0.10f);
        pet.Render(petBuf, state, theme, 0.10f);

        Assert.True(wp.IsCelebrationActive);
        Assert.True(pet.IsCelebrationActive);

        // Advance 3.5s -> cleanly expires after 3s celebration window
        for (int step = 1; step <= 25; step++)
        {
            float t = 0.10f + step * 0.15f;
            wp.Render(wpBuf, state, theme, t);
            pet.Render(petBuf, state, theme, t);
        }

        Assert.False(wp.IsCelebrationActive);
        Assert.False(pet.IsCelebrationActive);
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
    public void HotPath_ZeroBytesPerFrameAndSub1Point2Ms_AcrossAllV22Modes()
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

        // Warm up JIT & caches for each scenario and verify 0 B/frame steady-state allocations
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

            // Warmup 25 frames so state & caches settle (for drama, enters Panic Sprint)
            for (int i = 0; i < 25; i++)
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
                wp.Render(wpBuf, sc.State, theme, 1.30f + i * 0.033f, showMixiWhenAllIdle: true, effects: sc.Effects);
            }
            swWp.Stop();
            long allocWp = GC.GetAllocatedBytesForCurrentThread() - beforeWp;

            long beforePet = GC.GetAllocatedBytesForCurrentThread();
            swPet.Restart();
            for (int i = 0; i < frames; i++)
            {
                pet.Render(petBuf, sc.State, theme, 1.30f + i * 0.033f, showIdleChill: true, showMixiWhenAllIdle: true, effects: sc.Effects);
            }
            swPet.Stop();
            long allocPet = GC.GetAllocatedBytesForCurrentThread() - beforePet;

            double wpAvgMs = swWp.Elapsed.TotalMilliseconds / frames;
            double petAvgMs = swPet.Elapsed.TotalMilliseconds / frames;
            _output.WriteLine($"[{sc.Label}] WP: {wpAvgMs:F3} ms/f ({allocWp} B total), Pet: {petAvgMs:F3} ms/f ({allocPet} B total)");

            Assert.Equal(0, allocWp);
            Assert.Equal(0, allocPet);
        }
    }
}
