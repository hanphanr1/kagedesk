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

public readonly struct SlotBinding
{
    public readonly int SlotIndex;
    public readonly string? AgentId;
    public readonly string SpriteKey;
    public readonly AgentState State;
    public readonly bool Occupied;
    public readonly int CenterX;
    public readonly int DeskY;

    public SlotBinding(int slotIndex, string? agentId, string spriteKey, AgentState state, bool occupied, int centerX, int deskY)
    {
        SlotIndex = slotIndex;
        AgentId = agentId;
        SpriteKey = spriteKey;
        State = state;
        Occupied = occupied;
        CenterX = centerX;
        DeskY = deskY;
    }
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
    public string? BoundAgentId;
    public string? TargetAgentId;
    public int TargetSlotIndex;
    public bool TargetBoundBySpriteKey;
    public string? TargetSpriteKey;

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
    public int ActiveHandoffStationIndex => _activeHandoffStation;
    public string? ActiveHandoffFromAgentId => _activeHandoffStation >= 0 ? _motions[_activeHandoffStation].BoundAgentId : null;
    public string? ActiveHandoffToAgentId => _activeHandoffStation >= 0 ? _motions[_activeHandoffStation].TargetAgentId : null;
    public long LastHandledHandoffSeq => _lastHandledHandoffSeq;

    public bool TryGetPendingHandoffEvent(HqState state, out HqEvent handoffEvent)
    {
        var events = state.PendingVisualEvents;
        if (events != null)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev.SequenceId > _lastHandledHandoffSeq &&
                    string.Equals(ev.Type, "handoff", StringComparison.OrdinalIgnoreCase))
                {
                    handoffEvent = ev;
                    return true;
                }
            }
        }

        handoffEvent = default!;
        return false;
    }

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
                FacingRight = true,
                TargetSlotIndex = -1
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
        ReadOnlySpan<SlotBinding> slots,
        int coreDeskY,
        int extraDeskY,
        ParticleSystem? particles = null,
        int rugOffsetY = 22,
        bool enableAmbientStroll = true)
    {
        // 0. Cancel/reset motion if a moving actor's slot changes or disappears
        for (int i = 0; i < MaxStations; i++)
        {
            if (_motions[i].IsInMotion)
            {
                bool cancel = false;
                if (i >= slots.Length || !slots[i].Occupied)
                {
                    cancel = true;
                }
                else if (!string.Equals(slots[i].AgentId, _motions[i].BoundAgentId, StringComparison.OrdinalIgnoreCase))
                {
                    cancel = true;
                }

                if (cancel)
                {
                    ref var m = ref _motions[i];
                    m.State = ActorMotionState.AtDesk;
                    m.BehindDesk = true;
                    m.OffsetX = 0;
                    m.OffsetY = 0;
                    m.CarryingScroll = false;
                    m.IsFlashTrail = false;
                    m.BoundAgentId = null;
                    m.TargetAgentId = null;
                    m.TargetSlotIndex = -1;
                    m.TargetBoundBySpriteKey = false;
                    m.TargetSpriteKey = null;
                    if (_activeHandoffStation == i)
                    {
                        _activeHandoffStation = -1;
                    }
                }
            }
        }

        // 1. Check for new handoff visual events
        int newlyStarted = CheckForHandoffEvents(state, slots, coreDeskY, extraDeskY, rugOffsetY);

        // 2. Check for ambient idle life stroll
        if (newlyStarted < 0 && enableAmbientStroll)
        {
            newlyStarted = CheckForAmbientStroll(dt, state, slots, coreDeskY, rugOffsetY);
        }

        // 3. Update all active motions (do not advance on the frame it was triggered)
        for (int i = 0; i < MaxStations; i++)
        {
            if (_motions[i].State != ActorMotionState.AtDesk && i != newlyStarted)
            {
                UpdateStationMotion(i, dt, timeSeconds, slots, particles, rugOffsetY);
            }
        }
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
        int total = 4 + Math.Clamp(extraCount, 0, 5);
        Span<SlotBinding> slots = new SlotBinding[total];
        static string DefaultCoreSprite(int i) => i switch { 0 => "shikamaru", 1 => "minato", 2 => "tsunade", _ => "kakashi" };

        for (int i = 0; i < 4; i++)
        {
            int cx = i < coreCentersX.Length ? coreCentersX[i] : 56 + i * 90;
            string sprite = DefaultCoreSprite(i);
            AgentSnapshot? found = null;
            for (int a = 0; a < state.Agents.Count; a++)
            {
                var ag = state.Agents[a];
                if (string.Equals(ag.SpriteKey, sprite, StringComparison.OrdinalIgnoreCase) ||
                    (i == 0 && ag.Role == AgentRole.Leader) ||
                    (i == 1 && ag.Role == AgentRole.Coder) ||
                    (i == 2 && ag.Role == AgentRole.Tester) ||
                    (i == 3 && ag.Role == AgentRole.Reviewer))
                {
                    found = ag;
                    break;
                }
            }
            slots[i] = new SlotBinding(i, found?.Id, sprite, found?.State ?? AgentState.Idle, found != null, cx, coreDeskY);
        }

        for (int i = 0; i < extraCount; i++)
        {
            int cx = getExtraCenterX(i, extraCount);
            int agentIdx = 4 + i;
            AgentSnapshot? found = agentIdx < state.Agents.Count ? state.Agents[agentIdx] : null;
            slots[4 + i] = new SlotBinding(4 + i, found?.Id, found?.SpriteKey ?? "clone", found?.State ?? AgentState.Idle, found != null, cx, extraDeskY);
        }

        Update(dt, timeSeconds, state, slots, coreDeskY, extraDeskY, particles);
    }

    private int CheckForHandoffEvents(
        HqState state,
        ReadOnlySpan<SlotBinding> slots,
        int coreDeskY,
        int extraDeskY,
        int rugOffsetY)
    {
        if (_activeHandoffStation >= 0)
            return -1; // Handoff already in progress

        var events = state.PendingVisualEvents;
        if (events == null) return -1;

        for (int e = 0; e < events.Count; e++)
        {
            var ev = events[e];
            if (ev.SequenceId > _lastHandledHandoffSeq &&
                string.Equals(ev.Type, "handoff", StringComparison.OrdinalIgnoreCase))
            {
                _lastHandledHandoffSeq = ev.SequenceId;

                int fromStation = ResolveStationIndex(ev.AgentId, state, slots, ev.FromIsConcreteId);
                int toStation = ResolveStationIndex(ev.TargetAgentId, state, slots, ev.ToIsConcreteId);

                if (fromStation >= 0 && toStation >= 0 && fromStation != toStation)
                {
                    bool sameConcreteAgent =
                        !string.IsNullOrWhiteSpace(slots[fromStation].AgentId) &&
                        !string.IsNullOrWhiteSpace(slots[toStation].AgentId) &&
                        string.Equals(slots[fromStation].AgentId, slots[toStation].AgentId, StringComparison.OrdinalIgnoreCase);

                    if (!sameConcreteAgent)
                    {
                        StartHandoff(fromStation, toStation, slots, coreDeskY, extraDeskY, rugOffsetY);
                        return fromStation;
                    }
                }
            }
        }
        return -1;
    }

    private void StartHandoff(
        int fromStation,
        int toStation,
        ReadOnlySpan<SlotBinding> slots,
        int coreDeskY,
        int extraDeskY,
        int rugOffsetY)
    {
        ref readonly var fromSlot = ref slots[fromStation];
        ref readonly var toSlot = ref slots[toStation];

        int fromDeskX = fromSlot.CenterX;
        int fromDeskY = fromSlot.DeskY;
        int toDeskX = toSlot.CenterX;
        int toDeskY = toSlot.DeskY;

        int fromRugY = fromDeskY + rugOffsetY;
        int toRugY = toDeskY + rugOffsetY;
        int targetArrivalX = toDeskX + (fromDeskX < toDeskX ? -16 : 16);

        bool isMinato = string.Equals(fromSlot.SpriteKey, "minato", StringComparison.OrdinalIgnoreCase);

        ref var m = ref _motions[fromStation];
        m.State = ActorMotionState.StepOut;
        m.Elapsed = 0f;
        m.Duration = 0.35f;
        m.DeskX = fromDeskX;
        m.DeskY = fromDeskY;
        m.RugY = fromRugY;
        m.TargetX = targetArrivalX;
        m.TargetY = toRugY;
        m.OffsetX = 0;
        m.OffsetY = 0;
        m.FacingRight = toDeskX >= fromDeskX;
        m.WalkPhase = 0;
        m.CarryingScroll = true;
        m.IsFlashTrail = isMinato;
        m.BehindDesk = true;
        m.BoundAgentId = fromSlot.AgentId;
        m.TargetSlotIndex = toStation;

        if (!string.IsNullOrWhiteSpace(toSlot.AgentId))
        {
            // Concrete recipient bound: track ONLY this exact concrete agent ID on subsequent frames.
            m.TargetAgentId = toSlot.AgentId;
            m.TargetBoundBySpriteKey = false;
            m.TargetSpriteKey = null;
        }
        else
        {
            m.TargetAgentId = null;
            m.TargetBoundBySpriteKey = true;
            m.TargetSpriteKey = toSlot.SpriteKey;
        }

        _activeHandoffStation = fromStation;
    }

    private int CheckForAmbientStroll(
        float dt,
        HqState state,
        ReadOnlySpan<SlotBinding> slots,
        int coreDeskY,
        int rugOffsetY)
    {
        if (_activeHandoffStation >= 0) return -1;

        // Only stroll if all core stations are calm/idle and no alerts
        if (state.Mood != HqMood.Calm) return -1;
        var agents = state.Agents;
        for (int a = 0; a < agents.Count; a++)
        {
            var st = agents[a].State;
            if (st == AgentState.NeedsApproval || st == AgentState.Working) return -1;
        }

        _ambientTimer += dt;
        if (_ambientTimer >= _nextAmbientCooldown)
        {
            _ambientTimer = 0f;
            _nextAmbientCooldown = 22f + (NextPrng() % 1000) * 0.01f; // 22..32s

            // Choose a random core character (0..3)
            int candidate = (int)(NextPrng() % 4);
            if (!_motions[candidate].IsInMotion && candidate < slots.Length)
            {
                ref readonly var slot = ref slots[candidate];
                int deskX = slot.CenterX;
                int deskY = slot.DeskY;
                int rugY = deskY + rugOffsetY;
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
                m.BoundAgentId = slot.AgentId;
                return candidate;
            }
        }

        return -1;
    }

    private void UpdateStationMotion(
        int idx,
        float dt,
        float timeSeconds,
        ReadOnlySpan<SlotBinding> slots,
        ParticleSystem? particles,
        int rugOffsetY)
    {
        ref var m = ref _motions[idx];

        if (m.State is ActorMotionState.StepOut or ActorMotionState.WalkingToTarget or ActorMotionState.Delivering)
        {
            // 1. Revalidate sender binding
            if (idx >= slots.Length || !slots[idx].Occupied ||
                !string.Equals(slots[idx].AgentId, m.BoundAgentId, StringComparison.OrdinalIgnoreCase))
            {
                m.State = ActorMotionState.AtDesk;
                m.BehindDesk = true;
                m.OffsetX = 0;
                m.OffsetY = 0;
                m.CarryingScroll = false;
                m.IsFlashTrail = false;
                m.BoundAgentId = null;
                m.TargetAgentId = null;
                m.TargetSlotIndex = -1;
                m.TargetBoundBySpriteKey = false;
                m.TargetSpriteKey = null;
                if (_activeHandoffStation == idx) _activeHandoffStation = -1;
                return;
            }

            // Keep DeskX/DeskY synced if strip spacing adjusted while sender stayed at same slot index
            m.DeskX = slots[idx].CenterX;
            m.DeskY = slots[idx].DeskY;
            m.RugY = m.DeskY + rugOffsetY;

            // 2. Revalidate recipient binding
            if (m.CarryingScroll || m.TargetSlotIndex >= 0)
            {
                int currentRecipientSlot = -1;

                if (!string.IsNullOrEmpty(m.TargetAgentId))
                {
                    // Concrete agent ID bound: track ONLY this exact concrete agent ID.
                    // Never fall back to alias, role, or slot/sprite lookup.
                    for (int s = 0; s < slots.Length; s++)
                    {
                        if (slots[s].Occupied &&
                            string.Equals(slots[s].AgentId, m.TargetAgentId, StringComparison.OrdinalIgnoreCase))
                        {
                            currentRecipientSlot = s;
                            break;
                        }
                    }
                }
                else if (m.TargetBoundBySpriteKey && !string.IsNullOrEmpty(m.TargetSpriteKey))
                {
                    for (int s = 0; s < slots.Length; s++)
                    {
                        if (slots[s].Occupied &&
                            string.IsNullOrWhiteSpace(slots[s].AgentId) &&
                            string.Equals(slots[s].SpriteKey, m.TargetSpriteKey, StringComparison.OrdinalIgnoreCase))
                        {
                            currentRecipientSlot = s;
                            break;
                        }
                    }
                }

                if (currentRecipientSlot < 0)
                {
                    // Recipient disappeared or was replaced by a different agent: cancel delivery and walk back
                    m.State = ActorMotionState.WalkingBack;
                    m.Elapsed = 0f;
                    m.TargetX = m.DeskX + m.OffsetX;
                    m.TargetY = m.DeskY + m.OffsetY;
                    float dist = MathF.Abs(m.TargetX - m.DeskX);
                    m.Duration = m.IsFlashTrail ? 0.6f : Math.Clamp(dist / 70f, 0.45f, 1.4f);
                    m.FacingRight = m.DeskX >= m.TargetX;
                    m.CarryingScroll = false;
                    m.TargetSlotIndex = -1;
                }
                else
                {
                    ref readonly var recSlot = ref slots[currentRecipientSlot];
                    int toDeskX = recSlot.CenterX;
                    int toDeskY = recSlot.DeskY;
                    int toRugY = toDeskY + rugOffsetY;
                    int newTargetX = toDeskX + (m.DeskX < toDeskX ? -16 : 16);
                    int newTargetY = toRugY;

                    if (m.TargetSlotIndex != currentRecipientSlot || m.TargetX != newTargetX || m.TargetY != newTargetY)
                    {
                        m.TargetSlotIndex = currentRecipientSlot;
                        m.TargetX = newTargetX;
                        m.TargetY = newTargetY;
                        m.FacingRight = toDeskX >= (m.DeskX + m.OffsetX);

                        if (m.State == ActorMotionState.Delivering)
                        {
                            m.OffsetX = m.TargetX - m.DeskX;
                            m.OffsetY = m.TargetY - m.DeskY;
                        }
                    }
                }
            }
        }

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
                int currentY = (int)(m.RugY + p * (m.TargetY - m.RugY));
                m.OffsetY = currentY - m.DeskY;
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
                    m.OffsetY = m.TargetY - m.DeskY;
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
                m.OffsetY = m.TargetY - m.DeskY;
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
                int currentY = (int)(m.TargetY + p * (m.RugY - m.TargetY));
                m.OffsetY = currentY - m.DeskY;
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
                    m.OffsetY = m.RugY - m.DeskY;
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
                    m.BoundAgentId = null;
                    m.TargetAgentId = null;
                    m.TargetSlotIndex = -1;
                    m.TargetBoundBySpriteKey = false;
                    m.TargetSpriteKey = null;
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

    private static int ResolveStationIndex(string? agentId, HqState state, ReadOnlySpan<SlotBinding> slots, bool isConcreteId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return -1;
        string trimmed = agentId.Trim();

        // Pass 1: Strict exact OrdinalIgnoreCase lookup against occupied SlotBinding.AgentId
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Occupied && string.Equals(slots[i].AgentId, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // If this endpoint already resolved to a concrete agent ID when the handoff was queued,
        // never fall back to alias, role, or sprite lookup when that concrete agent is absent.
        if (isConcreteId)
        {
            return -1;
        }

        // Preserve exact-ID-first precedence across the snapshot: if a real agent literally has
        // this exact Id in state.Agents (even if not currently assigned a visible slot),
        // never shadow it with an alias match to another agent.
        var agents = state?.Agents;
        if (agents != null)
        {
            for (int i = 0; i < agents.Count; i++)
            {
                if (string.Equals(agents[i].Id, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return -1;
                }
            }
        }

        // Pass 2a: Shared canonical alias resolution against the snapshot
        if (AgentAliasResolver.TryResolveAgentId(trimmed, agents, out string? resolvedId) &&
            !string.IsNullOrEmpty(resolvedId))
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Occupied && string.Equals(slots[i].AgentId, resolvedId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        // Pass 2b: Fallback against SlotBinding.SpriteKey using the same shared canonical role alias helper
        if (AgentAliasResolver.TryResolveRoleAlias(trimmed, out var canonicalAlias))
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Occupied &&
                    string.Equals(slots[i].SpriteKey, canonicalAlias.SpriteKey, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private uint NextPrng()
    {
        _prngState ^= _prngState << 13;
        _prngState ^= _prngState >> 17;
        _prngState ^= _prngState << 5;
        return _prngState;
    }
}
