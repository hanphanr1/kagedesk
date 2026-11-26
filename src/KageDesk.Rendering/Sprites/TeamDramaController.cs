using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;

namespace KageDesk.Rendering.Sprites;

/// <summary>
/// Zero-allocation shared controller for KageDesk v2.2 Real Team Bot Drama:
/// 1) Review Reject / REQUEST_CHANGES: Kakashi throws a glowing red scroll across the desks/strip
///    to Minato, triggering a 6-second Panic Sprint (sweat drops, 2x typing, frantic sparks).
/// 2) Squad Celebration: 3-second coordinated celebratory hop + falling pixel confetti across
///    both Wallpaper HQ and Taskbar Pet.
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
    private string? _dramaFromId;
    private string? _dramaToId;
    private bool _dramaFromConcrete;
    private bool _dramaToConcrete;

    private float _panicRemaining;
    private float _impactFlashRemaining;
    private int _impactX;
    private int _impactY;
    private float _celebrationRemaining;

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
        _panicRemaining = 0f;
        _impactFlashRemaining = 0f;
        _celebrationRemaining = 0f;
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

    public bool IsSlotInPanicSprint(string? agentId, string spriteKey)
    {
        if (_panicRemaining <= 0f) return false;

        if (!string.IsNullOrEmpty(_dramaToId))
        {
            if (!string.IsNullOrEmpty(agentId) &&
                string.Equals(agentId, _dramaToId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!_dramaToConcrete &&
                string.Equals(spriteKey, _dramaToId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return !_dramaToConcrete && string.Equals(spriteKey, "minato", StringComparison.OrdinalIgnoreCase);
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
        bool emitTick)
    {
        if (dt < 0f) dt = 0f;
        if (dt > 0.2f) dt = 0.2f;

        // 1. Scan for new review_changes / drama events
        var events = state.PendingVisualEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.SequenceId > _lastHandledDramaSeq && IsDramaEventType(e.Type))
            {
                _lastHandledDramaSeq = e.SequenceId;
                StartRedScrollThrow(e, slots, canvasWidth);
            }
            else if (e.SequenceId > _lastHandledCelebrateSeq && IsCelebrateEventType(e.Type))
            {
                _lastHandledCelebrateSeq = e.SequenceId;
                _celebrationRemaining = CelebrationDuration;
                particles.EmitCelebrationConfettiShower(confettiMinX, confettiMaxX, confettiTopY, count: 22);
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
            // Keep target coordinates dynamically bound to live slot positions if present
            if (TryFindSlotPosition(slots, _dramaFromId, _dramaFromConcrete, "kakashi", out int fx, out int fy))
            {
                _fromX = fx;
                _fromY = fy - 14;
            }
            if (TryFindSlotPosition(slots, _dramaToId, _dramaToConcrete, "minato", out int tx, out int ty))
            {
                _toX = tx;
                _toY = ty - 14;
            }

            _scrollElapsed += dt;
            if (emitTick)
            {
                ComputeScrollPosition(Math.Clamp(_scrollElapsed / ScrollFlightDuration, 0f, 1f), out float sx, out float sy);
                particles.Emit(ParticleKind.Spark, sx, sy, 0f, -6f, 0.25f, CrimsonBright, 1);
            }

            if (_scrollElapsed >= ScrollFlightDuration)
            {
                _scrollActive = false;
                _panicRemaining = PanicSprintDuration;
                _impactFlashRemaining = 1.2f;
                _impactX = Math.Clamp((int)_toX, 16, canvasWidth - 16);
                _impactY = Math.Max(14, (int)_toY);
                particles.EmitSparks(_impactX, _impactY, 6);
                particles.EmitSweatDrops(_impactX, _impactY - 6, 4);
            }
        }

        // 3. Advance 6-second Minato Panic Sprint
        if (_panicRemaining > 0f)
        {
            _panicRemaining = MathF.Max(0f, _panicRemaining - dt);
            if (emitTick)
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
        }

        if (_impactFlashRemaining > 0f)
        {
            _impactFlashRemaining = MathF.Max(0f, _impactFlashRemaining - dt);
        }

        // 4. Advance 3-second Squad Celebration
        if (_celebrationRemaining > 0f)
        {
            _celebrationRemaining = MathF.Max(0f, _celebrationRemaining - dt);
            if (emitTick)
            {
                particles.EmitCelebrationConfettiShower(confettiMinX, confettiMaxX, confettiTopY, count: 6);
            }
        }
    }

    public void DrawOverlays(PixelBuffer buf, float timeSeconds)
    {
        if (_scrollActive)
        {
            float t = Math.Clamp(_scrollElapsed / ScrollFlightDuration, 0f, 1f);
            ComputeScrollPosition(t, out float fx, out float fy);
            int x = Math.Clamp((int)fx, 6, buf.Width - 7);
            int y = Math.Clamp((int)fy, 6, buf.Height - 7);

            // Glowing crimson aura trail behind the thrown scroll
            if (t > 0.08f)
            {
                ComputeScrollPosition(t - 0.07f, out float tx1, out float ty1);
                buf.BlendRect(Math.Clamp((int)tx1 - 3, 2, buf.Width - 8), Math.Clamp((int)ty1 - 2, 2, buf.Height - 6), 6, 4, Crimson, 125);
            }
            if (t > 0.16f)
            {
                ComputeScrollPosition(t - 0.14f, out float tx2, out float ty2);
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
            int badgeW = 48;
            int badgeH = 11;
            int bx = Math.Clamp(_impactX - badgeW / 2, 4, buf.Width - badgeW - 4);
            int by = Math.Clamp(_impactY - 22, 4, buf.Height - badgeH - 4);
            bool flash = (((int)(timeSeconds * 12f)) & 1) == 0;
            buf.DrawPixelCard(bx, by, badgeW, badgeH, CrimsonDark, flash ? CrimsonBright : Crimson);
            PixelFont.DrawTextCentered(buf, bx + badgeW / 2, by + 3, "FIX PR!", White, PixelFontSize.Small);
        }
    }

    private void StartRedScrollThrow(HqEvent e, ReadOnlySpan<SlotBinding> slots, int canvasWidth)
    {
        _dramaFromId = e.AgentId;
        _dramaToId = e.TargetAgentId;
        _dramaFromConcrete = e.FromIsConcreteId;
        _dramaToConcrete = e.ToIsConcreteId;

        bool foundFrom = TryFindSlotPosition(slots, _dramaFromId, _dramaFromConcrete, "kakashi", out int fx, out int fy);
        bool foundTo = TryFindSlotPosition(slots, _dramaToId, _dramaToConcrete, "minato", out int tx, out int ty);

        // If concrete ID was requested and is missing from live slots, do not mis-target another agent
        if ((_dramaFromConcrete && !foundFrom) || (_dramaToConcrete && !foundTo))
        {
            return;
        }

        if (!foundFrom)
        {
            fx = Math.Min(canvasWidth - 28, 328);
            fy = 104;
        }
        if (!foundTo)
        {
            tx = Math.Max(42, fx - 120);
            ty = fy;
        }

        _fromX = fx;
        _fromY = fy - 14;
        _toX = tx;
        _toY = ty - 14;
        _scrollActive = true;
        _scrollElapsed = 0f;
    }

    private void ComputeScrollPosition(float t, out float x, out float y)
    {
        x = _fromX + (_toX - _fromX) * t;
        float linearY = _fromY + (_toY - _fromY) * t;
        // Parabolic arc peaking 28 pixels above the straight line
        float arc = -4f * 28f * t * (1f - t);
        y = linearY + arc;
    }

    private static bool TryFindSlotPosition(
        ReadOnlySpan<SlotBinding> slots,
        string? agentId,
        bool isConcrete,
        string fallbackSprite,
        out int cx,
        out int deskY)
    {
        if (!string.IsNullOrEmpty(agentId))
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (!string.IsNullOrEmpty(slots[i].AgentId) &&
                    string.Equals(slots[i].AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                {
                    cx = slots[i].CenterX;
                    deskY = slots[i].DeskY;
                    return true;
                }
            }

            if (isConcrete)
            {
                cx = 0;
                deskY = 0;
                return false;
            }
        }

        // Alias / sprite fallback (only when not concrete ID)
        for (int i = 0; i < slots.Length; i++)
        {
            if (string.Equals(slots[i].SpriteKey, agentId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(slots[i].SpriteKey, fallbackSprite, StringComparison.OrdinalIgnoreCase))
            {
                cx = slots[i].CenterX;
                deskY = slots[i].DeskY;
                return true;
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
