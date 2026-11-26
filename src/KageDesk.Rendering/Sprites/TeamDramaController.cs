using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;

namespace KageDesk.Rendering.Sprites;

/// <summary>
/// Zero-allocation shared controller for KageDesk v2.2 Real Team Bot Drama:
/// 1) Review Reject / REQUEST_CHANGES: bound reviewer throws a glowing red scroll across the desks/strip
///    to ONE single bound target actor, triggering a 6-second Panic Sprint (sweat drops, 2x typing, sparks).
/// 2) Squad Celebration: 3-second coordinated celebratory hop + falling pixel confetti across
///    both Wallpaper HQ and Taskbar Pet.
/// Supports being shared by both HqWallpaperRenderer and TaskbarPetRenderer with synchronized single-advance
/// timeline stepping and per-canvas overlay coordinates.
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
    private float _fromX;
    private float _fromY;
    private float _toX;
    private float _toY;

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
    private int _impactX;
    private int _impactY;
    private float _celebrationRemaining;

    // Shared-instance timeline deduplication across Wallpaper + Taskbar Pet
    private float _lastAdvanceTimeSeconds = float.NaN;
    private int _lastAdvanceRendererToken = -1;
    private int _timelineEpoch;
    private int _impactEpoch = -1;
    private int _celebrateBurstEpoch = -1;

    public long LastHandledDramaSeq => _lastHandledDramaSeq;
    public long LastHandledCelebrateSeq => _lastHandledCelebrateSeq;
    public bool IsRedScrollFlying => _scrollActive;
    public bool IsPanicSprintActive => _panicRemaining > 0f;
    public bool IsDramaActive => _scrollActive || _panicRemaining > 0f;
    public bool IsCelebrationActive => _celebrationRemaining > 0f;
    public string? DramaFromAgentId => _dramaFromId;
    public string? DramaToAgentId => _dramaToId;
    public bool DramaFromIsConcreteId => _dramaFromConcrete;
    public bool DramaToIsConcreteId => _dramaToConcrete;
    public string? BoundFromAgentId => _boundFromAgentId;
    public string? BoundTargetAgentId => _boundTargetAgentId;
    public string? BoundTargetSpriteKey => _boundTargetSpriteKey;

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
        _lastAdvanceTimeSeconds = float.NaN;
        _lastAdvanceRendererToken = -1;
        _timelineEpoch = 0;
        _impactEpoch = -1;
        _celebrateBurstEpoch = -1;
    }

    /// <summary>
    /// Starts a bounded one-shot celebration window (used when multiple agents transition to Done together).
    /// </summary>
    public void StartCelebrationWindow()
    {
        _celebrationRemaining = CelebrationDuration;
        _celebrateBurstEpoch = _timelineEpoch;
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

    public void MarkDramaEventHandled(long seqId)
    {
        if (seqId > _lastHandledDramaSeq)
            _lastHandledDramaSeq = seqId;
    }

    /// <summary>
    /// Returns true ONLY for the single concrete bound target actor of the active Panic Sprint.
    /// </summary>
    public bool IsSlotInPanicSprint(string? agentId, string spriteKey)
    {
        if (_panicRemaining <= 0f) return false;

        if (!string.IsNullOrEmpty(_boundTargetAgentId))
        {
            return !string.IsNullOrEmpty(agentId) &&
                   string.Equals(agentId, _boundTargetAgentId, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(_boundTargetSpriteKey))
        {
            return string.IsNullOrEmpty(agentId) &&
                   string.Equals(spriteKey, _boundTargetSpriteKey, StringComparison.OrdinalIgnoreCase);
        }

        return false;
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
        if (dt < 0f) dt = 0f;
        if (dt > 0.2f) dt = 0.2f;

        // When shared between Wallpaper (token=1) and TaskbarPet (token=2), advance timers at most once per
        // render timestamp while still allowing each renderer to emit local particles to its own ParticleSystem.
        bool alreadyAdvancedThisTimestamp =
            rendererToken != 0 &&
            _lastAdvanceRendererToken != 0 &&
            _lastAdvanceRendererToken != rendererToken &&
            !float.IsNaN(_lastAdvanceTimeSeconds) &&
            MathF.Abs(timeSeconds - _lastAdvanceTimeSeconds) <= 0.0005f;

        if (!alreadyAdvancedThisTimestamp)
        {
            _lastAdvanceTimeSeconds = timeSeconds;
            _lastAdvanceRendererToken = rendererToken;
            _timelineEpoch++;

            // 1. Scan for new review_changes / drama & celebrate events (respecting consumption gates)
            var events = state.PendingVisualEvents;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (allowConsumeDrama && e.SequenceId > _lastHandledDramaSeq && IsDramaEventType(e.Type))
                {
                    _lastHandledDramaSeq = e.SequenceId;
                    TryStartRedScrollThrow(e, slots);
                }
                else if (allowConsumeCelebrate && e.SequenceId > _lastHandledCelebrateSeq && IsCelebrateEventType(e.Type))
                {
                    _lastHandledCelebrateSeq = e.SequenceId;
                    _celebrationRemaining = CelebrationDuration;
                    _celebrateBurstEpoch = _timelineEpoch;
                }
            }

            // Demo Studio -> celebrate keeps celebration gently topped up during demo preview
            if (string.Equals(state.DemoScenario, "celebrate", StringComparison.OrdinalIgnoreCase) && _celebrationRemaining < 0.5f)
            {
                _celebrationRemaining = CelebrationDuration;
            }

            // 2. Advance red scroll parabolic flight
            if (_scrollActive)
            {
                _scrollElapsed += dt;
                if (_scrollElapsed >= ScrollFlightDuration)
                {
                    _scrollActive = false;
                    _panicRemaining = PanicSprintDuration;
                    _impactFlashRemaining = 1.2f;
                    _impactEpoch = _timelineEpoch;
                }
            }

            // 3. Advance 6-second Panic Sprint
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

            // 4. Advance 3-second Squad Celebration
            if (_celebrationRemaining > 0f)
            {
                _celebrationRemaining = MathF.Max(0f, _celebrationRemaining - dt);
            }
        }
        else if (allowConsumeDrama || allowConsumeCelebrate)
        {
            // Second renderer at the same timestamp: if the first renderer was gated from consuming
            // (e.g. had no matching slots or was blocked), allow this renderer to consume pending events.
            var events = state.PendingVisualEvents;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (allowConsumeDrama && e.SequenceId > _lastHandledDramaSeq && IsDramaEventType(e.Type))
                {
                    _lastHandledDramaSeq = e.SequenceId;
                    TryStartRedScrollThrow(e, slots);
                }
                else if (allowConsumeCelebrate && e.SequenceId > _lastHandledCelebrateSeq && IsCelebrateEventType(e.Type))
                {
                    _lastHandledCelebrateSeq = e.SequenceId;
                    _celebrationRemaining = CelebrationDuration;
                    _celebrateBurstEpoch = _timelineEpoch;
                }
            }
        }

        // Per-renderer particle emission (safe for both standalone and shared instances)
        if (_celebrateBurstEpoch == _timelineEpoch)
        {
            particles.EmitCelebrationConfettiShower(confettiMinX, confettiMaxX, confettiTopY, count: 22);
        }

        if (_scrollActive)
        {
            ResolveScrollEndpoints(slots, canvasWidth, out float fx, out float fy, out float tx, out float ty);
            _fromX = fx;
            _fromY = fy;
            _toX = tx;
            _toY = ty;
            if (emitTick)
            {
                ComputeScrollPosition(fx, fy, tx, ty, Math.Clamp(_scrollElapsed / ScrollFlightDuration, 0f, 1f), out float sx, out float sy);
                particles.Emit(ParticleKind.Spark, sx, sy, 0f, -6f, 0.25f, CrimsonBright, 1);
            }
        }

        if (_impactEpoch == _timelineEpoch)
        {
            ResolveTargetImpactPoint(slots, canvasWidth, out int ix, out int iy);
            _impactX = ix;
            _impactY = iy;
            particles.EmitSparks(ix, iy, 6);
            particles.EmitSweatDrops(ix, iy - 6, 4);
        }

        if (_panicRemaining > 0f && emitTick)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                ref readonly var s = ref slots[i];
                if (IsSlotInPanicSprint(s.AgentId, s.SpriteKey))
                {
                    particles.EmitSweatDrops(s.CenterX, s.DeskY - 18, 2);
                    particles.EmitSparks(s.CenterX, s.DeskY - 6, 3);
                    break;
                }
            }
        }

        if (_celebrationRemaining > 0f && emitTick)
        {
            particles.EmitCelebrationConfettiShower(confettiMinX, confettiMaxX, confettiTopY, count: 6);
        }
    }

    public void DrawOverlays(PixelBuffer buf, float timeSeconds) =>
        DrawOverlays(buf, timeSeconds, ReadOnlySpan<SlotBinding>.Empty);

    public void DrawOverlays(PixelBuffer buf, float timeSeconds, ReadOnlySpan<SlotBinding> slots)
    {
        if (_scrollActive)
        {
            ResolveScrollEndpoints(slots, buf.Width, out float fromX, out float fromY, out float toX, out float toY);
            float t = Math.Clamp(_scrollElapsed / ScrollFlightDuration, 0f, 1f);
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

        if (_impactFlashRemaining > 0f)
        {
            ResolveTargetImpactPoint(slots, buf.Width, out int impactX, out int impactY);
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

        _fromX = fromSlot.CenterX;
        _fromY = fromSlot.DeskY - 14;
        _toX = toSlot.CenterX;
        _toY = toSlot.DeskY - 14;
        _scrollActive = true;
        _scrollElapsed = 0f;
        return true;
    }

    private void ResolveScrollEndpoints(
        ReadOnlySpan<SlotBinding> slots,
        int canvasWidth,
        out float fromX,
        out float fromY,
        out float toX,
        out float toY)
    {
        if (TryFindBoundSlot(slots, _boundFromAgentId, _boundFromSpriteKey, out int fx, out int fy))
        {
            fromX = fx;
            fromY = fy - 14;
        }
        else
        {
            fromX = _fromX > 0f ? Math.Clamp(_fromX, 16, canvasWidth - 16) : Math.Min(canvasWidth - 28, 328);
            fromY = _fromY > 0f ? _fromY : 90f;
        }

        if (TryFindBoundSlot(slots, _boundTargetAgentId, _boundTargetSpriteKey, out int tx, out int ty))
        {
            toX = tx;
            toY = ty - 14;
        }
        else
        {
            toX = _toX > 0f ? Math.Clamp(_toX, 16, canvasWidth - 16) : Math.Max(42, fromX - 120);
            toY = _toY > 0f ? _toY : fromY;
        }
    }

    private void ResolveTargetImpactPoint(ReadOnlySpan<SlotBinding> slots, int canvasWidth, out int impactX, out int impactY)
    {
        if (TryFindBoundSlot(slots, _boundTargetAgentId, _boundTargetSpriteKey, out int tx, out int ty))
        {
            impactX = Math.Clamp(tx, 16, canvasWidth - 16);
            impactY = Math.Max(14, ty - 14);
            return;
        }

        impactX = _impactX > 0 ? Math.Clamp(_impactX, 16, canvasWidth - 16) : Math.Clamp((int)_toX, 16, canvasWidth - 16);
        impactY = _impactY > 0 ? Math.Max(14, _impactY) : Math.Max(14, (int)_toY);
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
