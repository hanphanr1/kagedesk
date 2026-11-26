using System.Diagnostics;
using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;

namespace KageDesk.Rendering.Sprites;

/// <summary>
/// Zero-allocation read-only snapshot of central TeamDramaController visual state
/// consumed independently by HqWallpaperRenderer and TaskbarPetRenderer.
/// </summary>
public readonly record struct DramaEffectSnapshot(
    bool IsRedScrollFlying,
    float ScrollProgress,
    bool IsPanicSprintActive,
    bool IsImpactFlashActive,
    bool IsCelebrationActive,
    string? BoundFromAgentId,
    string? BoundFromSpriteKey,
    string? BoundTargetAgentId,
    string? BoundTargetSpriteKey,
    int CelebrationBurstSeq,
    int ImpactEventSeq
)
{
    public bool IsDramaActive => IsRedScrollFlying || IsPanicSprintActive;
}

/// <summary>
/// Zero-allocation shared controller for KageDesk v2.2 Real Team Bot Drama:
/// 1) Review Reject / REQUEST_CHANGES: bound reviewer throws a glowing red scroll across the desks/strip
///    to ONE single bound target actor, triggering a 6-second Panic Sprint (sweat drops, 2x typing, sparks).
/// 2) Squad Celebration: 3-second coordinated celebratory hop + falling pixel confetti across
///    both Wallpaper HQ and Taskbar Pet.
/// Advances strictly ONCE against a single monotonic clock timestamp (AdvanceTo(long monotonicTicks))
/// so interleaved Wallpaper + Taskbar Pet WinForms timers NEVER double-tick durations, and exposes a
/// read-only DramaEffectSnapshot so renderers never clobber shared coordinates or prematurely consume
/// events that another renderer can display.
/// </summary>
public sealed class TeamDramaController
{
    public const float ScrollFlightDuration = 0.90f;
    public const float PanicSprintDuration = 6.0f;
    public const float CelebrationDuration = 3.0f;

    private static readonly int Outline = ColorUtil.FromRgb(14, 16, 24);
    private static readonly int Crimson = ColorUtil.FromRgb(239, 68, 68);
    private static readonly int CrimsonBright = ColorUtil.FromRgb(252, 165, 165);
    private static readonly int CrimsonDark = ColorUtil.FromRgb(127, 29, 29);
    private static readonly int Gold = ColorUtil.FromRgb(250, 204, 21);
    private static readonly int White = ColorUtil.FromRgb(248, 250, 252);

    private long _lastHandledDramaSeq = -1;
    private long _lastHandledCelebrateSeq = -1;

    private bool _scrollActive;
    private float _scrollElapsed;

    // Active drama request metadata (never overwritten until a new drama resolves cleanly)
    private string? _dramaFromId;
    private string? _dramaToId;
    private bool _dramaFromConcrete;
    private bool _dramaToConcrete;

    // Single concrete bound source & target actor for active scroll + Panic Sprint
    private string? _boundFromAgentId;
    private string? _boundFromSpriteKey;
    private string? _boundTargetAgentId;
    private string? _boundTargetSpriteKey;

    private float _panicRemaining;
    private float _impactFlashRemaining;
    private float _celebrationRemaining;

    // Single monotonic timeline clock shared across all renderers
    private long _lastMonotonicTicks = -1;
    private int _celebrationBurstSeq;
    private int _impactEventSeq;

    // Fallback sequence cursors used only by legacy direct Update() callers
    private int _legacyCelebrateBurstSeen;
    private int _legacyImpactEventSeen;

    public long LastHandledDramaSeq => _lastHandledDramaSeq;
    public long LastHandledCelebrateSeq => _lastHandledCelebrateSeq;
    public bool IsRedScrollFlying => _scrollActive;
    public bool IsPanicSprintActive => _panicRemaining > 0f;
    public bool IsDramaActive => _scrollActive || _panicRemaining > 0f;
    public bool IsCelebrationActive => _celebrationRemaining > 0f;
    public float CelebrationRemainingSeconds => _celebrationRemaining;
    public float PanicRemainingSeconds => _panicRemaining;
    public string? DramaFromAgentId => _dramaFromId;
    public string? DramaToAgentId => _dramaToId;
    public bool DramaFromIsConcreteId => _dramaFromConcrete;
    public bool DramaToIsConcreteId => _dramaToConcrete;
    public string? BoundFromAgentId => _boundFromAgentId;
    public string? BoundTargetAgentId => _boundTargetAgentId;
    public string? BoundTargetSpriteKey => _boundTargetSpriteKey;

    public static long SecondsToMonotonicTicks(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0)
            return 0L;
        return (long)Math.Round(seconds * Stopwatch.Frequency);
    }

    public void Reset()
    {
        _lastHandledDramaSeq = -1;
        _lastHandledCelebrateSeq = -1;
        _scrollActive = false;
        _scrollElapsed = 0f;
        _dramaFromId = null;
        _dramaToId = null;
        _dramaFromConcrete = false;
        _dramaToConcrete = false;
        _boundFromAgentId = null;
        _boundFromSpriteKey = null;
        _boundTargetAgentId = null;
        _boundTargetSpriteKey = null;
        _panicRemaining = 0f;
        _impactFlashRemaining = 0f;
        _celebrationRemaining = 0f;
        _lastMonotonicTicks = -1;
        _celebrationBurstSeq = 0;
        _impactEventSeq = 0;
        _legacyCelebrateBurstSeen = 0;
        _legacyImpactEventSeen = 0;
    }

    /// <summary>
    /// Returns a zero-allocation read-only snapshot of all currently active drama/celebration effects.
    /// </summary>
    public DramaEffectSnapshot GetSnapshot() => new(
        IsRedScrollFlying: _scrollActive,
        ScrollProgress: _scrollActive ? Math.Clamp(_scrollElapsed / ScrollFlightDuration, 0f, 1f) : 0f,
        IsPanicSprintActive: _panicRemaining > 0f,
        IsImpactFlashActive: _impactFlashRemaining > 0f,
        IsCelebrationActive: _celebrationRemaining > 0f,
        BoundFromAgentId: _boundFromAgentId,
        BoundFromSpriteKey: _boundFromSpriteKey,
        BoundTargetAgentId: _boundTargetAgentId,
        BoundTargetSpriteKey: _boundTargetSpriteKey,
        CelebrationBurstSeq: _celebrationBurstSeq,
        ImpactEventSeq: _impactEventSeq);

    /// <summary>
    /// Advances all active drama/celebration timers ONCE against a single monotonic clock timestamp.
    /// Computes dt = max(0, now - _lastMonotonicTicks) / Stopwatch.Frequency, so interleaved calls from
    /// independent Wallpaper and Taskbar Pet WinForms timers NEVER double-tick durations!
    /// </summary>
    public float AdvanceTo(long monotonicTicks)
    {
        if (monotonicTicks < 0) monotonicTicks = 0;

        if (_lastMonotonicTicks < 0)
        {
            _lastMonotonicTicks = monotonicTicks;
            return 0f;
        }

        long deltaTicks = monotonicTicks - _lastMonotonicTicks;
        if (deltaTicks <= 0)
        {
            return 0f;
        }

        _lastMonotonicTicks = monotonicTicks;
        float dt = (float)((double)deltaTicks / Stopwatch.Frequency);
        if (dt > 0.2f) dt = 0.2f;

        // 1. Advance red scroll parabolic flight
        if (_scrollActive)
        {
            _scrollElapsed += dt;
            if (_scrollElapsed >= ScrollFlightDuration)
            {
                _scrollActive = false;
                _panicRemaining = PanicSprintDuration;
                _impactFlashRemaining = 1.2f;
                _impactEventSeq++;
            }
        }

        // 2. Advance 6-second Panic Sprint
        if (_panicRemaining > 0f)
        {
            _panicRemaining = MathF.Max(0f, _panicRemaining - dt);
            if (_panicRemaining <= 0f)
            {
                // Clear stale bound actor once Panic Sprint completes
                _boundFromAgentId = null;
                _boundFromSpriteKey = null;
                _boundTargetAgentId = null;
                _boundTargetSpriteKey = null;
            }
        }

        if (_impactFlashRemaining > 0f)
        {
            _impactFlashRemaining = MathF.Max(0f, _impactFlashRemaining - dt);
        }

        // 3. Advance 3-second Squad Celebration
        if (_celebrationRemaining > 0f)
        {
            _celebrationRemaining = MathF.Max(0f, _celebrationRemaining - dt);
        }

        return dt;
    }

    /// <summary>
    /// Starts a bounded one-shot celebration window (used when multiple agents transition to Done together).
    /// </summary>
    public void StartCelebrationWindow()
    {
        if (_celebrationRemaining >= CelebrationDuration - 0.15f)
            return;

        _celebrationRemaining = CelebrationDuration;
        _celebrationBurstSeq++;
    }

    public bool TryGetPendingDramaEvent(HqState state, out HqEvent pending)
    {
        var events = state.PendingVisualEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.SequenceId > _lastHandledDramaSeq && IsDramaEventType(e.Type))
            {
                pending = e;
                return true;
            }
        }

        pending = default!;
        return false;
    }

    public bool HasActiveOrPendingVisualDrama(HqState state)
    {
        if (IsDramaActive || IsCelebrationActive)
            return true;

        var events = state.PendingVisualEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.SequenceId > _lastHandledDramaSeq && IsDramaEventType(e.Type))
                return true;
            if (e.SequenceId > _lastHandledCelebrateSeq && IsCelebrateEventType(e.Type))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true ONLY for the single concrete bound target actor of the active Panic Sprint.
    /// </summary>
    public bool IsSlotInPanicSprint(string? agentId, string spriteKey)
    {
        if (_panicRemaining <= 0f) return false;
        return MatchesBoundActor(agentId, spriteKey, _boundTargetAgentId, _boundTargetSpriteKey);
    }

    public static bool IsSlotInPanicSprint(in DramaEffectSnapshot snapshot, string? agentId, string spriteKey)
    {
        if (!snapshot.IsPanicSprintActive) return false;
        return MatchesBoundActor(agentId, spriteKey, snapshot.BoundTargetAgentId, snapshot.BoundTargetSpriteKey);
    }

    private static bool MatchesBoundActor(string? agentId, string spriteKey, string? boundAgentId, string? boundSpriteKey)
    {
        if (!string.IsNullOrEmpty(boundAgentId))
        {
            return !string.IsNullOrEmpty(agentId) &&
                   string.Equals(agentId, boundAgentId, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(boundSpriteKey))
        {
            return string.IsNullOrEmpty(agentId) &&
                   string.Equals(spriteKey, boundSpriteKey, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Advances the single monotonic timeline to <paramref name="monotonicTicks"/>, centrally resolves
    /// pending drama/celebration events (ONLY consuming a drama event when BOTH endpoints actually resolve
    /// so a renderer with a subset of slots can NEVER globally swallow an event before another renderer sees it!),
    /// and returns a pure read-only <see cref="DramaEffectSnapshot"/>.
    /// </summary>
    public DramaEffectSnapshot AdvanceAndConsume(
        long monotonicTicks,
        HqState state,
        ReadOnlySpan<SlotBinding> slots,
        bool allowConsumeDrama = true,
        bool allowConsumeCelebrate = true)
    {
        // 1. Advance existing timers once against the single monotonic clock
        AdvanceTo(monotonicTicks);

        // 2. Scan for new review_changes / drama & celebrate events
        var events = state.PendingVisualEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (allowConsumeDrama && e.SequenceId > _lastHandledDramaSeq && IsDramaEventType(e.Type))
            {
                // ONLY mark consumed if endpoints actually resolved in this slot set!
                // If this renderer's subset of slots lacked the endpoints, leave it unconsumed so
                // the full-scene renderer (e.g. Wallpaper HQ) still receives and plays it!
                if (TryStartRedScrollThrow(e, slots))
                {
                    _lastHandledDramaSeq = e.SequenceId;
                }
            }
            else if (allowConsumeCelebrate && e.SequenceId > _lastHandledCelebrateSeq && IsCelebrateEventType(e.Type))
            {
                _lastHandledCelebrateSeq = e.SequenceId;
                _celebrationRemaining = CelebrationDuration;
                _celebrationBurstSeq++;
            }
        }

        // Demo Studio -> celebrate keeps celebration gently topped up during demo preview
        if (string.Equals(state.DemoScenario, "celebrate", StringComparison.OrdinalIgnoreCase) && _celebrationRemaining < 0.5f)
        {
            _celebrationRemaining = CelebrationDuration;
        }

        return GetSnapshot();
    }

    /// <summary>
    /// Pure read-only local particle emission for a renderer reading <paramref name="snapshot"/>.
    /// Never mutates TeamDramaController state or shared coordinates!
    /// </summary>
    public static void EmitLocalParticles(
        in DramaEffectSnapshot snapshot,
        ReadOnlySpan<SlotBinding> slots,
        ParticleSystem particles,
        int canvasWidth,
        int confettiMinX,
        int confettiMaxX,
        int confettiTopY,
        bool emitTick,
        ref int lastSeenCelebrateBurstSeq,
        ref int lastSeenImpactEventSeq)
    {
        if (snapshot.CelebrationBurstSeq != lastSeenCelebrateBurstSeq)
        {
            lastSeenCelebrateBurstSeq = snapshot.CelebrationBurstSeq;
            if (snapshot.IsCelebrationActive)
            {
                particles.EmitCelebrationConfettiShower(confettiMinX, confettiMaxX, confettiTopY, count: 22);
            }
        }

        if (snapshot.IsRedScrollFlying && emitTick)
        {
            ResolveScrollEndpoints(in snapshot, slots, canvasWidth, out float fx, out float fy, out float tx, out float ty);
            ComputeScrollPosition(fx, fy, tx, ty, snapshot.ScrollProgress, out float sx, out float sy);
            particles.Emit(ParticleKind.Spark, sx, sy, 0f, -6f, 0.25f, CrimsonBright, 1);
        }

        if (snapshot.ImpactEventSeq != lastSeenImpactEventSeq)
        {
            lastSeenImpactEventSeq = snapshot.ImpactEventSeq;
            if (snapshot.IsImpactFlashActive || snapshot.IsPanicSprintActive)
            {
                ResolveTargetImpactPoint(in snapshot, slots, canvasWidth, out int ix, out int iy);
                particles.EmitSparks(ix, iy, 6);
                particles.EmitSweatDrops(ix, iy - 6, 4);
            }
        }

        if (snapshot.IsPanicSprintActive && emitTick)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                ref readonly var s = ref slots[i];
                if (IsSlotInPanicSprint(in snapshot, s.AgentId, s.SpriteKey))
                {
                    particles.EmitSweatDrops(s.CenterX, s.DeskY - 18, 2);
                    particles.EmitSparks(s.CenterX, s.DeskY - 6, 3);
                    break;
                }
            }
        }

        if (snapshot.IsCelebrationActive && emitTick)
        {
            particles.EmitCelebrationConfettiShower(confettiMinX, confettiMaxX, confettiTopY, count: 6);
        }
    }

    public void Update(
        float dt,
        float timeSeconds,
        HqState state,
        ReadOnlySpan<SlotBinding> slots,
        ParticleSystem particles,
        int canvasWidth,
        int confettiMinX,
        int confettiMaxX,
        int confettiTopY,
        bool emitTick,
        bool allowConsumeDrama = true,
        bool allowConsumeCelebrate = true,
        int rendererToken = 0)
    {
        _ = dt;
        _ = rendererToken;

        long ticks = SecondsToMonotonicTicks(timeSeconds);
        DramaEffectSnapshot snapshot = AdvanceAndConsume(ticks, state, slots, allowConsumeDrama, allowConsumeCelebrate);
        EmitLocalParticles(
            in snapshot,
            slots,
            particles,
            canvasWidth,
            confettiMinX,
            confettiMaxX,
            confettiTopY,
            emitTick,
            ref _legacyCelebrateBurstSeen,
            ref _legacyImpactEventSeen);
    }

    public void DrawOverlays(PixelBuffer buf, float timeSeconds) =>
        DrawOverlays(buf, timeSeconds, ReadOnlySpan<SlotBinding>.Empty);

    public void DrawOverlays(PixelBuffer buf, float timeSeconds, ReadOnlySpan<SlotBinding> slots)
    {
        DramaEffectSnapshot snapshot = GetSnapshot();
        DrawOverlays(buf, timeSeconds, in snapshot, slots);
    }

    public static void DrawOverlays(
        PixelBuffer buf,
        float timeSeconds,
        in DramaEffectSnapshot snapshot,
        ReadOnlySpan<SlotBinding> slots)
    {
        if (snapshot.IsRedScrollFlying)
        {
            ResolveScrollEndpoints(in snapshot, slots, buf.Width, out float fromX, out float fromY, out float toX, out float toY);
            float t = snapshot.ScrollProgress;
            ComputeScrollPosition(fromX, fromY, toX, toY, t, out float fx, out float fy);
            int x = Math.Clamp((int)fx, 6, buf.Width - 7);
            int y = Math.Clamp((int)fy, 6, buf.Height - 7);

            // Glowing crimson aura trail behind the thrown scroll
            if (t > 0.08f)
            {
                ComputeScrollPosition(fromX, fromY, toX, toY, t - 0.07f, out float tx1, out float ty1);
                buf.BlendRect(Math.Clamp((int)tx1 - 3, 2, buf.Width - 8), Math.Clamp((int)ty1 - 2, 2, buf.Height - 6), 6, 4, Crimson, 125);
            }
            if (t > 0.16f)
            {
                ComputeScrollPosition(fromX, fromY, toX, toY, t - 0.14f, out float tx2, out float ty2);
                buf.BlendRect(Math.Clamp((int)tx2 - 2, 2, buf.Width - 6), Math.Clamp((int)ty2 - 1, 2, buf.Height - 4), 4, 3, CrimsonBright, 85);
            }

            // Outer glowing crimson halo
            buf.DrawCircle(x, y, 7, ColorUtil.WithAlpha(Crimson, 105), true);
            buf.DrawCircle(x, y, 4, ColorUtil.WithAlpha(CrimsonBright, 155), true);

            // Spinning red review-reject scroll body
            bool vertical = (((int)(timeSeconds * 16f)) & 1) == 0;
            if (vertical)
            {
                buf.FillRect(x - 3, y - 5, 7, 10, Outline);
                buf.FillRect(x - 2, y - 4, 5, 8, Crimson);
                buf.FillRect(x - 2, y - 4, 5, 1, Gold);
                buf.FillRect(x - 2, y + 3, 5, 1, Gold);
                buf.FillRect(x - 1, y - 1, 3, 2, White);
            }
            else
            {
                buf.FillRect(x - 5, y - 3, 10, 7, Outline);
                buf.FillRect(x - 4, y - 2, 8, 5, Crimson);
                buf.FillRect(x - 4, y - 2, 1, 5, Gold);
                buf.FillRect(x + 3, y - 2, 1, 5, Gold);
                buf.FillRect(x - 1, y - 1, 2, 3, White);
            }
        }

        if (snapshot.IsImpactFlashActive)
        {
            ResolveTargetImpactPoint(in snapshot, slots, buf.Width, out int impactX, out int impactY);
            int badgeW = 48;
            int badgeH = 11;
            int bx = Math.Clamp(impactX - badgeW / 2, 4, buf.Width - badgeW - 4);
            int by = Math.Clamp(impactY - 22, 4, buf.Height - badgeH - 4);
            bool flash = (((int)(timeSeconds * 12f)) & 1) == 0;
            buf.DrawPixelCard(bx, by, badgeW, badgeH, CrimsonDark, flash ? CrimsonBright : Crimson);
            PixelFont.DrawTextCentered(buf, bx + badgeW / 2, by + 3, "FIX PR!", White, PixelFontSize.Small);
        }
    }

    private bool TryStartRedScrollThrow(HqEvent e, ReadOnlySpan<SlotBinding> slots)
    {
        // Resolve BOTH endpoints into local variables first; NEVER mutate active drama state on failed lookup!
        if (!TryResolveSingleSlot(slots, e.AgentId, e.FromIsConcreteId, "kakashi", excludeIndex: -1, out int fromIdx))
        {
            return false;
        }

        if (!TryResolveSingleSlot(slots, e.TargetAgentId, e.ToIsConcreteId, "minato", excludeIndex: fromIdx, out int toIdx))
        {
            return false;
        }

        if (fromIdx == toIdx)
        {
            return false;
        }

        ref readonly var fromSlot = ref slots[fromIdx];
        ref readonly var toSlot = ref slots[toIdx];

        _dramaFromId = e.AgentId;
        _dramaToId = e.TargetAgentId;
        _dramaFromConcrete = e.FromIsConcreteId;
        _dramaToConcrete = e.ToIsConcreteId;

        _boundFromAgentId = fromSlot.AgentId;
        _boundFromSpriteKey = fromSlot.SpriteKey;
        _boundTargetAgentId = toSlot.AgentId;
        _boundTargetSpriteKey = toSlot.SpriteKey;

        _scrollActive = true;
        _scrollElapsed = 0f;
        return true;
    }

    private static void ResolveScrollEndpoints(
        in DramaEffectSnapshot snapshot,
        ReadOnlySpan<SlotBinding> slots,
        int canvasWidth,
        out float fromX,
        out float fromY,
        out float toX,
        out float toY)
    {
        if (TryFindBoundSlot(slots, snapshot.BoundFromAgentId, snapshot.BoundFromSpriteKey, out int fx, out int fy))
        {
            fromX = fx;
            fromY = fy - 14;
        }
        else
        {
            fromX = Math.Min(canvasWidth - 28, 328);
            fromY = 90f;
        }

        if (TryFindBoundSlot(slots, snapshot.BoundTargetAgentId, snapshot.BoundTargetSpriteKey, out int tx, out int ty))
        {
            toX = tx;
            toY = ty - 14;
        }
        else
        {
            toX = Math.Max(42, fromX - 120);
            toY = fromY;
        }
    }

    private static void ResolveTargetImpactPoint(
        in DramaEffectSnapshot snapshot,
        ReadOnlySpan<SlotBinding> slots,
        int canvasWidth,
        out int impactX,
        out int impactY)
    {
        if (TryFindBoundSlot(slots, snapshot.BoundTargetAgentId, snapshot.BoundTargetSpriteKey, out int tx, out int ty))
        {
            impactX = Math.Clamp(tx, 16, canvasWidth - 16);
            impactY = Math.Max(14, ty - 14);
            return;
        }

        impactX = Math.Clamp(canvasWidth / 2, 16, canvasWidth - 16);
        impactY = 76;
    }

    private static void ComputeScrollPosition(float fromX, float fromY, float toX, float toY, float t, out float x, out float y)
    {
        x = fromX + (toX - fromX) * t;
        float linearY = fromY + (toY - fromY) * t;
        // Parabolic arc peaking 28 pixels above the straight line
        float arc = -4f * 28f * t * (1f - t);
        y = linearY + arc;
    }

    /// <summary>
    /// Resolves a single concrete slot using strict precedence:
    /// 1) exact AgentId match,
    /// 2) explicit non-concrete alias match on SpriteKey (no arbitrary fallback),
    /// 3) intentional omitted endpoint fallback sprite.
    /// </summary>
    private static bool TryResolveSingleSlot(
        ReadOnlySpan<SlotBinding> slots,
        string? requestedId,
        bool isConcrete,
        string fallbackSprite,
        int excludeIndex,
        out int slotIndex)
    {
        if (slots.Length == 0)
        {
            slotIndex = -1;
            return false;
        }

        if (!string.IsNullOrEmpty(requestedId))
        {
            // 1. Exact concrete AgentId match
            for (int i = 0; i < slots.Length; i++)
            {
                if (i == excludeIndex) continue;
                if (!string.IsNullOrEmpty(slots[i].AgentId) &&
                    string.Equals(slots[i].AgentId, requestedId, StringComparison.OrdinalIgnoreCase))
                {
                    slotIndex = i;
                    return true;
                }
            }

            if (isConcrete)
            {
                slotIndex = -1;
                return false;
            }

            // 2. Explicit non-concrete alias match on SpriteKey (prefer occupied slot first)
            int unoccupiedMatch = -1;
            for (int i = 0; i < slots.Length; i++)
            {
                if (i == excludeIndex) continue;
                if (string.Equals(slots[i].SpriteKey, requestedId, StringComparison.OrdinalIgnoreCase))
                {
                    if (slots[i].Occupied)
                    {
                        slotIndex = i;
                        return true;
                    }
                    if (unoccupiedMatch < 0)
                    {
                        unoccupiedMatch = i;
                    }
                }
            }

            if (unoccupiedMatch >= 0)
            {
                slotIndex = unoccupiedMatch;
                return true;
            }

            slotIndex = -1;
            return false;
        }

        // 3. Omitted endpoint -> intentional default fallback sprite (prefer occupied slot first)
        int defaultUnoccupied = -1;
        for (int i = 0; i < slots.Length; i++)
        {
            if (i == excludeIndex) continue;
            if (string.Equals(slots[i].SpriteKey, fallbackSprite, StringComparison.OrdinalIgnoreCase))
            {
                if (slots[i].Occupied)
                {
                    slotIndex = i;
                    return true;
                }
                if (defaultUnoccupied < 0)
                {
                    defaultUnoccupied = i;
                }
            }
        }

        if (defaultUnoccupied >= 0)
        {
            slotIndex = defaultUnoccupied;
            return true;
        }

        slotIndex = -1;
        return false;
    }

    private static bool TryFindBoundSlot(
        ReadOnlySpan<SlotBinding> slots,
        string? boundAgentId,
        string? boundSpriteKey,
        out int cx,
        out int deskY)
    {
        if (!string.IsNullOrEmpty(boundAgentId))
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (!string.IsNullOrEmpty(slots[i].AgentId) &&
                    string.Equals(slots[i].AgentId, boundAgentId, StringComparison.OrdinalIgnoreCase))
                {
                    cx = slots[i].CenterX;
                    deskY = slots[i].DeskY;
                    return true;
                }
            }
        }
        else if (!string.IsNullOrEmpty(boundSpriteKey))
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (string.IsNullOrEmpty(slots[i].AgentId) &&
                    string.Equals(slots[i].SpriteKey, boundSpriteKey, StringComparison.OrdinalIgnoreCase))
                {
                    cx = slots[i].CenterX;
                    deskY = slots[i].DeskY;
                    return true;
                }
            }
        }

        cx = 0;
        deskY = 0;
        return false;
    }

    public static bool IsDramaEventType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return string.Equals(type, "review_changes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "drama", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "request_changes", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCelebrateEventType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return string.Equals(type, "celebrate", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "BuildPassed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "pr_merged", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "ci_pass", StringComparison.OrdinalIgnoreCase);
    }
}
