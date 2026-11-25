using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;

namespace KageDesk.Rendering.Sprites;

public enum ActorMotionState : byte
{
    AtDesk,
    StepOut,
    WalkingToTarget,
    Delivering,
    WalkingBack,
    StepBack,
    IdleStroll
}

public struct ActorMotion
{
    public ActorMotionState State;
    public float Elapsed;
    public float Duration;
    public int DeskX;
    public int DeskY;
    public int RugY;
    public int TargetX;
    public int TargetY;
    public int OffsetX;
    public int OffsetY;
    public bool FacingRight;
    public int WalkPhase;
    public bool CarryingScroll;
    public bool IsFlashTrail;
    public bool BehindDesk;

    public readonly bool IsInMotion => State != ActorMotionState.AtDesk;
}

public sealed class ActorMotionSystem
{
    public const int MaxStations = 9;

    private readonly ActorMotion[] _motions = new ActorMotion[MaxStations];
    private long _lastHandledHandoffSeq = -1;
    private float _ambientTimer = 0f;
    private float _nextAmbientCooldown = 26f;
    private uint _prngState = 849311;
    private int _activeHandoffStation = -1;

    public bool IsHandoffActive => _activeHandoffStation >= 0;

    public ActorMotionSystem()
    {
        Reset();
    }

    public void Reset()
    {
        for (int i = 0; i < MaxStations; i++)
        {
            _motions[i] = new ActorMotion
            {
                State = ActorMotionState.AtDesk,
                BehindDesk = true,
                FacingRight = true
            };
        }
        _lastHandledHandoffSeq = -1;
        _ambientTimer = 0f;
        _nextAmbientCooldown = 26f;
        _activeHandoffStation = -1;
    }

    public ref readonly ActorMotion GetMotion(int stationIndex)
    {
        if ((uint)stationIndex >= MaxStations)
            return ref _motions[0];
        return ref _motions[stationIndex];
    }

    public void Update(
        float dt,
        float timeSeconds,
        HqState state,
        int coreDeskY,
        int extraDeskY,
        int extraCount,
        ReadOnlySpan<int> coreCentersX,
        Func<int, int, int> getExtraCenterX,
        ParticleSystem? particles = null)
    {
        // 1. Check for new handoff visual events
        int newlyStarted = CheckForHandoffEvents(state, coreDeskY, extraDeskY, extraCount, coreCentersX, getExtraCenterX);

        // 2. Check for ambient idle life stroll
        if (newlyStarted < 0)
        {
            newlyStarted = CheckForAmbientStroll(dt, state, coreDeskY, coreCentersX);
        }

        // 3. Update all active motions (do not advance on the frame it was triggered)
        for (int i = 0; i < MaxStations; i++)
        {
            if (_motions[i].State != ActorMotionState.AtDesk && i != newlyStarted)
            {
                UpdateStationMotion(i, dt, timeSeconds, particles);
            }
        }
    }

    private int CheckForHandoffEvents(
        HqState state,
        int coreDeskY,
        int extraDeskY,
        int extraCount,
        ReadOnlySpan<int> coreCentersX,
        Func<int, int, int> getExtraCenterX)
    {
        if (_activeHandoffStation >= 0)
            return -1; // Handoff already in progress

        var events = state.PendingVisualEvents;
        for (int e = 0; e < events.Count; e++)
        {
            var ev = events[e];
            if (ev.SequenceId > _lastHandledHandoffSeq &&
                string.Equals(ev.Type, "handoff", StringComparison.OrdinalIgnoreCase))
            {
                _lastHandledHandoffSeq = ev.SequenceId;

                int fromStation = ResolveStationIndex(ev.AgentId, state, extraCount);
                int toStation = ResolveStationIndex(ev.TargetAgentId, state, extraCount);

                if (fromStation >= 0 && toStation >= 0 && fromStation != toStation)
                {
                    StartHandoff(fromStation, toStation, coreDeskY, extraDeskY, extraCount, coreCentersX, getExtraCenterX, state);
                    return fromStation;
                }
            }
        }
        return -1;
    }

    private void StartHandoff(
        int fromStation,
        int toStation,
        int coreDeskY,
        int extraDeskY,
        int extraCount,
        ReadOnlySpan<int> coreCentersX,
        Func<int, int, int> getExtraCenterX,
        HqState state)
    {
        int fromDeskX = GetStationX(fromStation, extraCount, coreCentersX, getExtraCenterX);
        int fromDeskY = fromStation < 4 ? coreDeskY : extraDeskY;
        int toDeskX = GetStationX(toStation, extraCount, coreCentersX, getExtraCenterX);
        int toDeskY = toStation < 4 ? coreDeskY : extraDeskY;

        int rugY = fromStation < 4 ? coreDeskY + 22 : extraDeskY + 22;
        int targetArrivalX = toDeskX + (fromDeskX < toDeskX ? -16 : 16);

        string fromSprite = GetStationSprite(fromStation, state);
        bool isMinato = string.Equals(fromSprite, "minato", StringComparison.OrdinalIgnoreCase);

        ref var m = ref _motions[fromStation];
        m.State = ActorMotionState.StepOut;
        m.Elapsed = 0f;
        m.Duration = 0.35f;
        m.DeskX = fromDeskX;
        m.DeskY = fromDeskY;
        m.RugY = rugY;
        m.TargetX = targetArrivalX;
        m.TargetY = rugY;
        m.OffsetX = 0;
        m.OffsetY = 0;
        m.FacingRight = toDeskX >= fromDeskX;
        m.WalkPhase = 0;
        m.CarryingScroll = true;
        m.IsFlashTrail = isMinato;
        m.BehindDesk = true;

        _activeHandoffStation = fromStation;
    }

    private int CheckForAmbientStroll(
        float dt,
        HqState state,
        int coreDeskY,
        ReadOnlySpan<int> coreCentersX)
    {
        if (_activeHandoffStation >= 0) return -1;

        // Only stroll if all core stations are calm/idle and no alerts
        if (state.Mood != HqMood.Calm) return -1;
        if (state.Agents.Any(a => a.State == AgentState.NeedsApproval || a.State == AgentState.Working)) return -1;

        _ambientTimer += dt;
        if (_ambientTimer >= _nextAmbientCooldown)
        {
            _ambientTimer = 0f;
            _nextAmbientCooldown = 22f + (NextPrng() % 1000) * 0.01f; // 22..32s

            // Choose a random core character (0..3)
            int candidate = (int)(NextPrng() % 4);
            if (!_motions[candidate].IsInMotion)
            {
                int deskX = coreCentersX[candidate];
                int deskY = coreDeskY;
                int rugY = coreDeskY + 22;
                int strollDist = ((int)(NextPrng() % 2) == 0 ? 1 : -1) * (18 + (int)(NextPrng() % 14));
                int targetX = Math.Clamp(deskX + strollDist, 30, 354);

                ref var m = ref _motions[candidate];
                m.State = ActorMotionState.IdleStroll;
                m.Elapsed = 0f;
                m.Duration = 3.0f; // 3-second stroll
                m.DeskX = deskX;
                m.DeskY = deskY;
                m.RugY = rugY;
                m.TargetX = targetX;
                m.TargetY = rugY;
                m.OffsetX = 0;
                m.OffsetY = 0;
                m.FacingRight = targetX >= deskX;
                m.WalkPhase = 0;
                m.CarryingScroll = false;
                m.IsFlashTrail = false;
                m.BehindDesk = true;
                return candidate;
            }
        }
        return -1;
    }

    private void UpdateStationMotion(int idx, float dt, float timeSeconds, ParticleSystem? particles)
    {
        ref var m = ref _motions[idx];
        m.Elapsed += dt;

        switch (m.State)
        {
            case ActorMotionState.StepOut:
            {
                float p = Math.Clamp(m.Elapsed / m.Duration, 0f, 1f);
                m.BehindDesk = p < 0.4f;
                m.OffsetX = 0;
                m.OffsetY = (int)(p * (m.RugY - m.DeskY));
                m.WalkPhase = ((int)(timeSeconds * 8f)) & 3;

                if (p >= 1f)
                {
                    m.State = ActorMotionState.WalkingToTarget;
                    m.Elapsed = 0f;
                    float dist = MathF.Abs(m.TargetX - m.DeskX);
                    // Minato flashes fast (0.55s), others walk at ~70 px/s
                    m.Duration = m.IsFlashTrail ? 0.55f : Math.Clamp(dist / 70f, 0.45f, 1.4f);
                    m.BehindDesk = false;
                }
                break;
            }

            case ActorMotionState.WalkingToTarget:
            {
                float p = Math.Clamp(m.Elapsed / m.Duration, 0f, 1f);
                m.BehindDesk = false;
                m.OffsetX = (int)(p * (m.TargetX - m.DeskX));
                m.OffsetY = m.RugY - m.DeskY;
                m.WalkPhase = ((int)(timeSeconds * 8f)) & 3;

                if (m.IsFlashTrail && particles != null && (int)(timeSeconds * 20f) % 2 == 0)
                {
                    // Golden Hiraishin spark trail
                    particles.EmitSparks(m.DeskX + m.OffsetX, m.DeskY + m.OffsetY + 3, 2);
                }

                if (p >= 1f)
                {
                    m.State = ActorMotionState.Delivering;
                    m.Elapsed = 0f;
                    m.Duration = 0.45f; // Exactly 0.45s delivery pause
                    m.OffsetX = m.TargetX - m.DeskX;
                    m.WalkPhase = 0;

                    // Spark burst upon scroll delivery
                    if (particles != null)
                    {
                        particles.EmitSparks(m.DeskX + m.OffsetX, m.DeskY + m.OffsetY, 5);
                    }
                }
                break;
            }

            case ActorMotionState.Delivering:
            {
                m.BehindDesk = false;
                m.OffsetX = m.TargetX - m.DeskX;
                m.OffsetY = m.RugY - m.DeskY;
                m.WalkPhase = 0;

                if (m.Elapsed >= m.Duration)
                {
                    m.State = ActorMotionState.WalkingBack;
                    m.Elapsed = 0f;
                    float dist = MathF.Abs(m.TargetX - m.DeskX);
                    m.Duration = m.IsFlashTrail ? 0.6f : Math.Clamp(dist / 70f, 0.45f, 1.4f);
                    m.FacingRight = m.DeskX >= m.TargetX;
                    m.CarryingScroll = false; // Scroll has been delivered!
                }
                break;
            }

            case ActorMotionState.WalkingBack:
            {
                float p = Math.Clamp(m.Elapsed / m.Duration, 0f, 1f);
                m.BehindDesk = false;
                m.OffsetX = (int)((1f - p) * (m.TargetX - m.DeskX));
                m.OffsetY = m.RugY - m.DeskY;
                m.WalkPhase = ((int)(timeSeconds * 8f)) & 3;

                if (m.IsFlashTrail && particles != null && (int)(timeSeconds * 20f) % 2 == 0)
                {
                    particles.EmitSparks(m.DeskX + m.OffsetX, m.DeskY + m.OffsetY + 3, 2);
                }

                if (p >= 1f)
                {
                    m.State = ActorMotionState.StepBack;
                    m.Elapsed = 0f;
                    m.Duration = 0.35f;
                    m.OffsetX = 0;
                    m.FacingRight = true;
                }
                break;
            }

            case ActorMotionState.StepBack:
            {
                float p = Math.Clamp(m.Elapsed / m.Duration, 0f, 1f);
                m.BehindDesk = p > 0.6f;
                m.OffsetX = 0;
                m.OffsetY = (int)((1f - p) * (m.RugY - m.DeskY));
                m.WalkPhase = ((int)(timeSeconds * 8f)) & 3;

                if (p >= 1f)
                {
                    m.State = ActorMotionState.AtDesk;
                    m.BehindDesk = true;
                    m.OffsetX = 0;
                    m.OffsetY = 0;
                    m.CarryingScroll = false;
                    m.IsFlashTrail = false;
                    m.FacingRight = true;
                    if (_activeHandoffStation == idx)
                    {
                        _activeHandoffStation = -1;
                    }
                }
                break;
            }

            case ActorMotionState.IdleStroll:
            {
                // 3-second stroll:
                // 0.0 .. 0.35s: StepOut
                // 0.35 .. 1.25s: Walk to TargetX
                // 1.25 .. 2.0s: Pause calmly at TargetX
                // 2.0 .. 2.65s: Walk back to DeskX
                // 2.65 .. 3.0s: StepBack
                float t = m.Elapsed;
                if (t < 0.35f)
                {
                    float p = t / 0.35f;
                    m.BehindDesk = p < 0.4f;
                    m.OffsetX = 0;
                    m.OffsetY = (int)(p * (m.RugY - m.DeskY));
                    m.WalkPhase = ((int)(timeSeconds * 6f)) & 3;
                }
                else if (t < 1.25f)
                {
                    float p = (t - 0.35f) / 0.9f;
                    m.BehindDesk = false;
                    m.OffsetX = (int)(p * (m.TargetX - m.DeskX));
                    m.OffsetY = m.RugY - m.DeskY;
                    m.FacingRight = m.TargetX >= m.DeskX;
                    m.WalkPhase = ((int)(timeSeconds * 6f)) & 3;
                }
                else if (t < 2.0f)
                {
                    m.BehindDesk = false;
                    m.OffsetX = m.TargetX - m.DeskX;
                    m.OffsetY = m.RugY - m.DeskY;
                    m.WalkPhase = 0;
                }
                else if (t < 2.65f)
                {
                    float p = (t - 2.0f) / 0.65f;
                    m.BehindDesk = false;
                    m.OffsetX = (int)((1f - p) * (m.TargetX - m.DeskX));
                    m.OffsetY = m.RugY - m.DeskY;
                    m.FacingRight = m.DeskX >= m.TargetX;
                    m.WalkPhase = ((int)(timeSeconds * 6f)) & 3;
                }
                else if (t < 3.0f)
                {
                    float p = (t - 2.65f) / 0.35f;
                    m.BehindDesk = p > 0.6f;
                    m.OffsetX = 0;
                    m.OffsetY = (int)((1f - p) * (m.RugY - m.DeskY));
                    m.FacingRight = true;
                    m.WalkPhase = ((int)(timeSeconds * 6f)) & 3;
                }
                else
                {
                    m.State = ActorMotionState.AtDesk;
                    m.BehindDesk = true;
                    m.OffsetX = 0;
                    m.OffsetY = 0;
                    m.FacingRight = true;
                }
                break;
            }
        }
    }

    private static int ResolveStationIndex(string? agentId, HqState state, int extraCount)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return -1;

        // Check if matches core character name or sprite or ID
        string lower = agentId.ToLowerInvariant();
        if (lower.Contains("shikamaru") || lower.Contains("leader")) return 0;
        if (lower.Contains("minato") || lower.Contains("coder")) return 1;
        if (lower.Contains("tsunade") || lower.Contains("tester") || lower.Contains("qa")) return 2;
        if (lower.Contains("kakashi") || lower.Contains("reviewer")) return 3;

        // Search live agents
        for (int i = 0; i < state.Agents.Count; i++)
        {
            var a = state.Agents[i];
            if (string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase))
            {
                if (a.Role == AgentRole.Leader) return 0;
                if (a.Role == AgentRole.Coder) return 1;
                if (a.Role == AgentRole.Tester) return 2;
                if (a.Role == AgentRole.Reviewer) return 3;
                // Otherwise extra slot
                int extraIdx = 4 + (i - 4);
                if (extraIdx >= 4 && extraIdx < 4 + extraCount) return extraIdx;
                return 4; // fallback first extra slot
            }
        }
        return -1;
    }

    private static int GetStationX(int idx, int extraCount, ReadOnlySpan<int> coreCentersX, Func<int, int, int> getExtraCenterX)
    {
        if (idx < 4) return coreCentersX[idx];
        return getExtraCenterX(idx - 4, extraCount);
    }

    private static string GetStationSprite(int idx, HqState state)
    {
        return idx switch
        {
            0 => "shikamaru",
            1 => "minato",
            2 => "tsunade",
            3 => "kakashi",
            _ => "clone"
        };
    }

    private uint NextPrng()
    {
        _prngState ^= _prngState << 13;
        _prngState ^= _prngState >> 17;
        _prngState ^= _prngState << 5;
        return _prngState;
    }
}
