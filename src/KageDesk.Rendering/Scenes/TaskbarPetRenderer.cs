using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Sprites;

namespace KageDesk.Rendering.Scenes;

public readonly record struct PetHitRegion(
    string ActionId,
    int X,
    int Y,
    int Width,
    int Height,
    string? Label = null,
    string? TargetAgentId = null,
    AgentState? TargetAgentState = null
)
{
    public bool Contains(int px, int py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
}

public sealed class TaskbarPetRenderer
{
    public const int CanvasWidth = 384;
    public const int CanvasHeight = 128;
    public const int GroundY = 124;

    private static readonly int ColorOutline = ColorUtil.FromRgb(14, 16, 24);
    private static readonly int ColorAmber = ColorUtil.FromRgb(245, 158, 11);
    private static readonly int ColorAmberBright = ColorUtil.FromRgb(253, 224, 71);
    private static readonly int ColorAmberCardBg = ColorUtil.FromRgb(24, 18, 28);
    private static readonly int ColorAmberHeaderBg = ColorUtil.FromRgb(48, 32, 16);
    private static readonly int ColorCrimson = ColorUtil.FromRgb(239, 68, 68);
    private static readonly int ColorCrimsonCardBg = ColorUtil.FromRgb(30, 14, 20);
    private static readonly int ColorEmerald = ColorUtil.FromRgb(34, 197, 94);
    private static readonly int ColorEmeraldCardBg = ColorUtil.FromRgb(14, 30, 22);
    private static readonly int ColorCyan = ColorUtil.FromRgb(34, 211, 238);
    private static readonly int ColorCyanCardBg = ColorUtil.FromRgb(12, 24, 38);
    private static readonly int ColorSlate = ColorUtil.FromRgb(148, 163, 184);
    private static readonly int ColorWhite = ColorUtil.FromRgb(248, 250, 252);
    private static readonly int ColorGold = ColorUtil.FromRgb(250, 204, 21);

    private readonly List<PetHitRegion> _hitRegions = new(16);
    private readonly ParticleSystem _particles = new();
    private readonly ActorMotionSystem _motionSystem = new();
    private readonly TeamDramaController _dramaController;
    private readonly SlotBinding[] _slotBindings = new SlotBinding[6];
    private readonly AgentSnapshot[] _workingScratch = new AgentSnapshot[6];
    private readonly AgentSnapshot[] _celebrateScratch = new AgentSnapshot[4];
    private readonly AgentSnapshot[] _idleScratch = new AgentSnapshot[2];
    private int _idleTerminalCount = 0;
    private AgentSnapshot? _idlePrimaryNonProcess = null;
    private readonly char[] _textScratch = new char[64];

    public TaskbarPetRenderer(TeamDramaController? sharedDramaController = null)
    {
        _dramaController = sharedDramaController ?? new TeamDramaController();
    }

    private struct WorkingCacheItem
    {
        public AgentSnapshot? Agent;
        public string? AgentId;
        public AgentActionKind ActionKind;
        public string ShortLabel;
        public string ShortName;
        public string HitRegionAction;
    }

    private readonly WorkingCacheItem[] _cachedWorkingItems = new WorkingCacheItem[6];
    private readonly string?[] _cachedCelebrateAgentIds = new string?[4];
    private readonly string?[] _cachedCelebrateActionIds = new string?[4];
    private string? _cachedSingleWorkingFocusActionId;
    private string? _cachedSingleWorkingShortName;
    private string? _cachedIdlePrimaryActionId;
    private readonly string?[] _cachedIdleTerminalActionIds = new string?[2];
    private string? _cachedIdleTopText;
    private string? _cachedWorkingMultiLabel;
    private HqState? _lastRenderedState;
    private int _lastStripCount;
    private int _lastDoneCount;

    private ThemePack? _cachedTheme;
    private ResolvedScenePalette _palette;
    private float _lastTime = -1f;
    private float _emitAccum;
    private DateTimeOffset _stateTimestamp;

    public IReadOnlyList<PetHitRegion> HitRegions => _hitRegions;
    public bool IsHandoffActive => _motionSystem.IsHandoffActive;
    public bool IsDramaActive => _dramaController.IsDramaActive;
    public bool IsCelebrationActive => _dramaController.IsCelebrationActive;
    public ActorMotionSystem MotionSystem => _motionSystem;
    public TeamDramaController DramaController => _dramaController;
    public bool IsMixiChillActive { get; private set; }
    public MixiChillActivity? ActiveMixiActivity { get; private set; }
    public string? ActiveAlertAgentId { get; private set; }
    public AgentState? ActiveAlertAgentState { get; private set; }

    public bool HasUnconsumedHandoffEvent(HqState state) =>
        _motionSystem.HasUnconsumedHandoffEvent(state) || _dramaController.HasActiveOrPendingVisualDrama(state);

    public PetHitRegion? HitTest(int px, int py)
    {
        for (int i = _hitRegions.Count - 1; i >= 0; i--)
        {
            if (_hitRegions[i].Contains(px, py))
            {
                return _hitRegions[i];
            }
        }
        return null;
    }

    public void ResetEffects()
    {
        _particles.Reset();
        _motionSystem.Reset();
        _dramaController.Reset();
        _lastTime = -1f;
        _emitAccum = 0f;
        _lastRenderedState = null;
        _lastStripCount = 0;
        _lastDoneCount = 0;
        Array.Clear(_cachedWorkingItems);
        Array.Clear(_cachedCelebrateAgentIds);
        Array.Clear(_cachedCelebrateActionIds);
        IsMixiChillActive = false;
        ActiveMixiActivity = null;
        ActiveAlertAgentId = null;
        ActiveAlertAgentState = null;
    }

    public IReadOnlyList<PetHitRegion> Render(
        PixelBuffer buf,
        HqState state,
        ThemePack theme,
        float timeSeconds,
        bool showIdleChill = true,
        DateTimeOffset? nowOverride = null,
        bool showMixiWhenAllIdle = true,
        IReadOnlyDictionary<string, AgentState>? dismissedAlerts = null,
        SceneEffectsState effects = default)
    {
        if (buf.Width < CanvasWidth || buf.Height < CanvasHeight)
        {
            throw new ArgumentException($"PixelBuffer must be at least {CanvasWidth}x{CanvasHeight}.", nameof(buf));
        }

        _stateTimestamp = nowOverride ?? state.Timestamp;

        if (!ReferenceEquals(_cachedTheme, theme))
        {
            _cachedTheme = theme;
            _palette = new ResolvedScenePalette(theme);
        }

        float dt = _lastTime < 0f ? 0.033f : Math.Clamp(timeSeconds - _lastTime, 0f, 0.2f);
        _lastTime = timeSeconds;

        bool isNight = state.Weather == EffectiveWeather.MidnightRain;

        buf.ClearTransparent();
        _hitRegions.Clear();
        IsMixiChillActive = false;
        ActiveMixiActivity = null;
        ActiveAlertAgentId = null;
        ActiveAlertAgentState = null;

        _emitAccum += dt;
        bool emitTick = false;
        if (_emitAccum >= 0.14f)
        {
            _emitAccum = 0f;
            emitTick = true;
        }

        // Demo Studio -> Do Mixi Chill Mode explicitly activates the exclusive 5-activity chill corner
        if (string.Equals(state.DemoScenario, "mixi", StringComparison.OrdinalIgnoreCase))
        {
            if (_motionSystem.IsHandoffActive) _motionSystem.Reset();
            _dramaController.Update(
                dt,
                timeSeconds,
                state,
                ReadOnlySpan<SlotBinding>.Empty,
                _particles,
                CanvasWidth,
                confettiMinX: 160,
                confettiMaxX: CanvasWidth - 10,
                confettiTopY: 20,
                emitTick: emitTick,
                allowConsumeDrama: false,
                allowConsumeCelebrate: false,
                rendererToken: 2);
            RenderMixiChillMode(buf, timeSeconds, isNight, effects);
            _particles.Update(dt);
            _particles.Render(buf);
            DrawWakeUpPulse(buf, timeSeconds, effects.WakeUpPulseRemainingSeconds);
            return _hitRegions;
        }

        AgentSnapshot? approvalAgent = null;
        AgentSnapshot? failedAgent = null;
        AgentSnapshot? doneAgent = null;
        int doneCount = 0;
        int workingCount = 0;
        int totalWorking = 0;

        var agents = state.Agents;
        for (int i = 0; i < agents.Count; i++)
        {
            AgentSnapshot ag = agents[i];
            bool isDismissedAlert = dismissedAlerts != null &&
                dismissedAlerts.TryGetValue(ag.Id, out var dismissedState) &&
                ag.State == dismissedState;

            if (ag.State == AgentState.NeedsApproval && !isDismissedAlert && approvalAgent == null)
            {
                approvalAgent = ag;
            }
            else if (ag.State == AgentState.Failed && !isDismissedAlert && failedAgent == null)
            {
                failedAgent = ag;
            }
            else if (ag.State == AgentState.Done)
            {
                if (doneAgent == null) doneAgent = ag;
                if (doneCount < 4) _celebrateScratch[doneCount++] = ag;
            }
            else if (ag.State == AgentState.Working)
            {
                totalWorking++;
                if (workingCount < 6)
                {
                    _workingScratch[workingCount++] = ag;
                }
            }
        }

        int stripCount = workingCount;

        // Phase 1: Always revalidate active motions on EVERY tick regardless of display mode or empty strip,
        // without scanning or consuming any new pending handoff events.
        if (_motionSystem.IsHandoffActive)
        {
            AgentSnapshot? activeFrom = FindExactAgentById(agents, _motionSystem.ActiveHandoffFromAgentId);
            AgentSnapshot? activeTo = FindExactAgentById(agents, _motionSystem.ActiveHandoffToAgentId);

            if (approvalAgent != null || failedAgent != null || activeFrom == null)
            {
                _motionSystem.RevalidateActiveMotions(ReadOnlySpan<SlotBinding>.Empty, rugOffsetY: 4);
            }
            else
            {
                EnsureAgentInStrip(activeFrom, activeTo, ref stripCount);
                EnsureAgentInStrip(activeTo, activeFrom, ref stripCount);

                int pinnedIdx = _motionSystem.ActiveHandoffStationIndex;
                if (pinnedIdx >= 0 && pinnedIdx < stripCount)
                {
                    int currentIdx = FindInStrip(activeFrom.Id, stripCount);
                    if (currentIdx >= 0 && currentIdx != pinnedIdx)
                    {
                        (_workingScratch[pinnedIdx], _workingScratch[currentIdx]) =
                            (_workingScratch[currentIdx], _workingScratch[pinnedIdx]);
                    }
                }

                BuildStripSlotBindings(stripCount);
                _motionSystem.RevalidateActiveMotions(_slotBindings.AsSpan(0, stripCount), rugOffsetY: 4);
            }
        }

        // Phase 2: Check for new unconsumed handoff event and prioritize BOTH endpoints into visible strip
        bool hasNewResolvableHandoff = false;
        if (!_motionSystem.IsHandoffActive && approvalAgent == null && failedAgent == null)
        {
            while (_motionSystem.TryGetPendingHandoffEvent(state, out var pendingHandoff))
            {
                AgentSnapshot? handoffFrom = ResolveHandoffAgentInSnapshot(pendingHandoff.AgentId, agents, pendingHandoff.FromIsConcreteId);
                AgentSnapshot? handoffTo = ResolveHandoffAgentInSnapshot(pendingHandoff.TargetAgentId, agents, pendingHandoff.ToIsConcreteId);
                if (handoffFrom != null && handoffTo != null &&
                    !string.Equals(handoffFrom.Id, handoffTo.Id, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureAgentInStrip(handoffFrom, handoffTo, ref stripCount);
                    EnsureAgentInStrip(handoffTo, handoffFrom, ref stripCount);
                    hasNewResolvableHandoff = true;
                    break;
                }

                _motionSystem.MarkHandoffEventHandled(pendingHandoff.SequenceId);
            }
        }

        // Phase 2b: Check for active/pending review_changes drama event and prioritize bound endpoints into visible strip!
        bool dramaMode = false;
        if (approvalAgent == null && failedAgent == null)
        {
            if (_dramaController.IsDramaActive)
            {
                string fromKey = _dramaController.BoundFromAgentId ?? _dramaController.DramaFromAgentId ?? "kakashi";
                bool fromConcrete = !string.IsNullOrEmpty(_dramaController.BoundFromAgentId) || _dramaController.DramaFromIsConcreteId;
                string toKey = _dramaController.BoundTargetAgentId ?? _dramaController.DramaToAgentId ?? "minato";
                bool toConcrete = !string.IsNullOrEmpty(_dramaController.BoundTargetAgentId) || _dramaController.DramaToIsConcreteId;

                AgentSnapshot? dramaFrom = ResolveHandoffAgentInSnapshot(fromKey, agents, fromConcrete);
                AgentSnapshot? dramaTo = ResolveHandoffAgentInSnapshot(toKey, agents, toConcrete);
                if (dramaFrom != null && dramaTo != null)
                {
                    EnsureAgentInStrip(dramaFrom, dramaTo, ref stripCount);
                    EnsureAgentInStrip(dramaTo, dramaFrom, ref stripCount);
                    dramaMode = true;
                }
            }
            else if (_dramaController.TryGetPendingDramaEvent(state, out var pendingDrama))
            {
                AgentSnapshot? dramaFrom = ResolveHandoffAgentInSnapshot(pendingDrama.AgentId ?? "kakashi", agents, pendingDrama.FromIsConcreteId);
                AgentSnapshot? dramaTo = ResolveHandoffAgentInSnapshot(pendingDrama.TargetAgentId ?? "minato", agents, pendingDrama.ToIsConcreteId);
                if (dramaFrom != null && dramaTo != null &&
                    !string.Equals(dramaFrom.Id, dramaTo.Id, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureAgentInStrip(dramaFrom, dramaTo, ref stripCount);
                    EnsureAgentInStrip(dramaTo, dramaFrom, ref stripCount);
                    dramaMode = true;
                }
                else
                {
                    _dramaController.MarkDramaEventHandled(pendingDrama.SequenceId);
                }
            }
        }

        bool handoffMode = _motionSystem.IsHandoffActive || hasNewResolvableHandoff;

        // Guarantee any non-working / empty-strip path clears lingering motions
        if (!handoffMode && (approvalAgent != null || failedAgent != null || stripCount == 0))
        {
            _motionSystem.RevalidateActiveMotions(ReadOnlySpan<SlotBinding>.Empty, rugOffsetY: 4);
        }

        // Detect state changes, strip count changes, OR same-count slot reorders
        bool stateOrStripChanged = !ReferenceEquals(state, _lastRenderedState) || stripCount != _lastStripCount;
        if (!stateOrStripChanged)
        {
            for (int i = 0; i < stripCount; i++)
            {
                if (!ReferenceEquals(_cachedWorkingItems[i].Agent, _workingScratch[i]) ||
                    !string.Equals(_cachedWorkingItems[i].AgentId, _workingScratch[i].Id, StringComparison.OrdinalIgnoreCase))
                {
                    stateOrStripChanged = true;
                    break;
                }
            }
        }

        if (stateOrStripChanged)
        {
            _lastRenderedState = state;
            _lastStripCount = stripCount;
            RefreshStripCaches(agents, stripCount, totalWorking);
        }

        // Trigger bounded 3s celebration window once on transition into 2+ Done agents
        if (approvalAgent == null && failedAgent == null && doneCount > 1 && _lastDoneCount <= 1)
        {
            _dramaController.StartCelebrationWindow();
        }
        _lastDoneCount = doneCount;

        // Build live strip slot bindings once so TeamDramaController runs EXACTLY ONCE per frame across all branches
        if (stripCount > 0)
        {
            BuildStripSlotBindings(stripCount);
        }

        bool allowConsumeDrama = approvalAgent == null && failedAgent == null && stripCount > 0;
        bool allowConsumeCelebrate = approvalAgent == null && failedAgent == null;
        ReadOnlySpan<SlotBinding> dramaSlots = allowConsumeDrama
            ? _slotBindings.AsSpan(0, stripCount)
            : ReadOnlySpan<SlotBinding>.Empty;

        int confettiSpanCount = Math.Max(stripCount, Math.Max(doneCount, 1));
        int confettiSpacing = confettiSpanCount == 2 ? 64 : 46;
        int confettiMinX = Math.Max(12, 346 - (confettiSpanCount - 1) * confettiSpacing - 24);

        _dramaController.Update(
            dt,
            timeSeconds,
            state,
            dramaSlots,
            _particles,
            CanvasWidth,
            confettiMinX: confettiMinX,
            confettiMaxX: CanvasWidth - 10,
            confettiTopY: 16,
            emitTick: emitTick,
            allowConsumeDrama: allowConsumeDrama,
            allowConsumeCelebrate: allowConsumeCelebrate,
            rendererToken: 2);

        if (allowConsumeDrama && _dramaController.IsDramaActive)
        {
            dramaMode = true;
        }

        bool isSquadCelebrationDemo = string.Equals(state.DemoScenario, "celebrate", StringComparison.OrdinalIgnoreCase);

        if (approvalAgent != null)
        {
            RenderApprovalMode(buf, approvalAgent, timeSeconds);
        }
        else if (failedAgent != null)
        {
            RenderFailedMode(buf, failedAgent, timeSeconds, emitTick);
        }
        else if ((handoffMode || dramaMode) && stripCount > 0)
        {
            RenderWorkingMode(buf, state, stripCount, Math.Max(totalWorking, stripCount), dt, timeSeconds, emitTick, consumeNewHandoffs: hasNewResolvableHandoff, isNight: isNight, effects: effects);
        }
        else if (isSquadCelebrationDemo || (doneCount > 1))
        {
            RenderSquadCelebrationMode(buf, state, doneCount, timeSeconds, emitTick, isNight, effects);
        }
        else if (doneAgent != null)
        {
            RenderDoneMode(buf, doneAgent, timeSeconds, emitTick);
        }
        else if (stripCount > 0)
        {
            RenderWorkingMode(buf, state, stripCount, totalWorking, dt, timeSeconds, emitTick, consumeNewHandoffs: false, isNight: isNight, effects: effects);
        }
        else if (showIdleChill)
        {
            if (_dramaController.IsCelebrationActive && agents.Count > 0)
            {
                int celCount = Math.Min(4, agents.Count);
                for (int i = 0; i < celCount; i++) _celebrateScratch[i] = agents[i];
                RenderSquadCelebrationMode(buf, state, celCount, timeSeconds, emitTick, isNight, effects);
            }
            else if (MixiChillRenderer.ShouldRenderMixiChillCorner(
                state,
                showMixiWhenAllIdle,
                _motionSystem.IsHandoffActive,
                _motionSystem.LastHandledHandoffSeq,
                _dramaController.IsDramaActive,
                _dramaController.IsCelebrationActive,
                _dramaController.LastHandledDramaSeq,
                _dramaController.LastHandledCelebrateSeq))
            {
                RenderMixiChillMode(buf, timeSeconds, isNight, effects);
            }
            else
            {
                RenderIdleChillMode(buf, state, timeSeconds, emitTick, isNight, effects);
            }
        }

        _dramaController.DrawOverlays(buf, timeSeconds, dramaSlots);
        _particles.Update(dt);
        _particles.Render(buf);
        DrawWakeUpPulse(buf, timeSeconds, effects.WakeUpPulseRemainingSeconds);

        return _hitRegions;
    }

    private void BuildStripSlotBindings(int stripCount)
    {
        int spacing = stripCount == 2 ? 68 : (stripCount <= 4 ? 48 : 38);
        const int rightmostX = 346;
        const int deskCharY = GroundY - 6;

        for (int i = 0; i < stripCount; i++)
        {
            AgentSnapshot ag = _workingScratch[i];
            int cx = rightmostX - i * spacing;
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);
            _slotBindings[i] = new SlotBinding(i, ag.Id, sprite, ag.State, occupied: true, centerX: cx, deskY: deskCharY);
        }
    }

    private static bool HasVerifiedProcessIdentity(AgentSnapshot ag)
    {
        return string.Equals(ag.Source, "process", StringComparison.OrdinalIgnoreCase) &&
               ag.ProcessId is > 0 &&
               ag.ProcessStartTicks is > 0;
    }

    private void RefreshStripCaches(IReadOnlyList<AgentSnapshot> agents, int stripCount, int totalWorking)
    {
        for (int i = 0; i < stripCount; i++)
        {
            var ag = _workingScratch[i];
            var (kind, label) = AgentActionClassifier.Classify(ag.State, ag.ToolName, ag.TaskTitle);
            _cachedWorkingItems[i] = new WorkingCacheItem
            {
                Agent = ag,
                AgentId = ag.Id,
                ActionKind = kind,
                ShortLabel = label,
                ShortName = ShortenName(ag.Name, ag.Id),
                HitRegionAction = $"agent:{ag.Id}"
            };
        }

        for (int i = stripCount; i < _cachedWorkingItems.Length; i++)
        {
            _cachedWorkingItems[i] = default;
        }

        if (stripCount == 1)
        {
            var single = _workingScratch[0];
            _cachedSingleWorkingShortName = _cachedWorkingItems[0].ShortName;
            _cachedSingleWorkingFocusActionId = string.Equals(single.Source, "process", StringComparison.OrdinalIgnoreCase)
                ? (HasVerifiedProcessIdentity(single) ? $"focus_agent:{single.Id}" : $"agent:{single.Id}")
                : "open_orca";
            _cachedWorkingMultiLabel = null;
        }
        else
        {
            _cachedSingleWorkingShortName = null;
            _cachedSingleWorkingFocusActionId = null;
            if (totalWorking > 6)
            {
                int extra = totalWorking - stripCount;
                _cachedWorkingMultiLabel = $"{stripCount} WORKING (+{extra})";
            }
            else
            {
                int shown = Math.Max(totalWorking, stripCount);
                _cachedWorkingMultiLabel = $"{shown} AGENTS WORKING";
            }
        }

        _idleTerminalCount = 0;
        _idlePrimaryNonProcess = null;
        for (int i = 0; i < agents.Count; i++)
        {
            AgentSnapshot ag = agents[i];
            if (ag.State == AgentState.Idle)
            {
                if (string.Equals(ag.Source, "process", StringComparison.OrdinalIgnoreCase))
                {
                    if (_idleTerminalCount < 2)
                    {
                        _idleScratch[_idleTerminalCount] = ag;
                        _cachedIdleTerminalActionIds[_idleTerminalCount] = $"agent:{ag.Id}";
                        _idleTerminalCount++;
                    }
                }
                else if (_idlePrimaryNonProcess == null)
                {
                    _idlePrimaryNonProcess = ag;
                }
            }
        }

        string primaryId = _idlePrimaryNonProcess?.Id ?? (_idlePrimaryNonProcess != null ? ShinobiSpriteRenderer.NormalizeSpriteKey(_idlePrimaryNonProcess.SpriteKey) : "kakashi");
        _cachedIdlePrimaryActionId = $"agent:{primaryId}";

        if (_idleTerminalCount > 0)
        {
            _cachedIdleTopText = _idleTerminalCount == 1
                ? $"{ShortenName(_idleScratch[0].Name, _idleScratch[0].Id)} READY"
                : $"{_idleTerminalCount} TERMINALS";
        }
        else
        {
            _cachedIdleTopText = null;
        }
    }

    private void EnsureAgentInStrip(AgentSnapshot? candidate, AgentSnapshot? protectedOther, ref int count)
    {
        if (candidate == null) return;
        if (FindInStrip(candidate.Id, count) >= 0) return;

        if (count < 6)
        {
            _workingScratch[count++] = candidate;
            return;
        }

        // Replace the last non-handoff slot so BOTH handoff sender and recipient remain visible
        for (int i = count - 1; i >= 0; i--)
        {
            if (protectedOther != null &&
                string.Equals(_workingScratch[i].Id, protectedOther.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _workingScratch[i] = candidate;
            return;
        }
    }

    private int FindInStrip(string agentId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(_workingScratch[i].Id, agentId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static AgentSnapshot? FindExactAgentById(IReadOnlyList<AgentSnapshot> agents, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        string trimmed = agentId.Trim();
        for (int i = 0; i < agents.Count; i++)
        {
            if (string.Equals(agents[i].Id, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return agents[i];
            }
        }
        return null;
    }

    private static AgentSnapshot? ResolveHandoffAgentInSnapshot(string? idOrAlias, IReadOnlyList<AgentSnapshot> agents, bool isConcreteId)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias)) return null;
        string trimmed = idOrAlias.Trim();

        // 1. Exact ID lookup always takes precedence
        var exact = FindExactAgentById(agents, trimmed);
        if (exact != null) return exact;

        // 2. Strict concrete-ID semantics: never fall back to alias/role/replacement when concrete ID is absent
        if (isConcreteId) return null;

        // 3. Shared canonical alias resolution
        if (AgentAliasResolver.TryResolveAgentId(trimmed, agents, out string? resolvedId) &&
            !string.IsNullOrEmpty(resolvedId))
        {
            return FindExactAgentById(agents, resolvedId);
        }

        if (AgentAliasResolver.TryResolveRoleAlias(trimmed, out var canonicalAlias))
        {
            for (int i = 0; i < agents.Count; i++)
            {
                if (string.Equals(ShinobiSpriteRenderer.NormalizeSpriteKey(agents[i].SpriteKey), canonicalAlias.SpriteKey, StringComparison.OrdinalIgnoreCase))
                {
                    return agents[i];
                }
            }
        }

        return null;
    }

    private void RenderApprovalMode(PixelBuffer buf, AgentSnapshot agent, float timeSeconds)
    {
        ActiveAlertAgentId = agent.Id;
        ActiveAlertAgentState = AgentState.NeedsApproval;

        const int charX = 344;
        string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(agent.SpriteKey);

        ShinobiSpriteRenderer.DrawCharacter(buf, sprite, AgentState.NeedsApproval, charX, GroundY, timeSeconds, behaviorVariant: agent.BehaviorVariant);
        _hitRegions.Add(new PetHitRegion($"agent:{agent.Id}", charX - 16, GroundY - 32, 32, 34, agent.Name));

        // Amber card bubble
        const int cardX = 96;
        const int cardY = 48;
        const int cardW = 208;
        const int cardH = 72;
        int border = ((int)(timeSeconds * 5f) & 1) == 0 ? ColorAmberBright : ColorAmber;

        buf.DrawPixelCard(cardX, cardY, cardW, cardH, ColorAmberCardBg, border);
        buf.FillRect(cardX + cardW - 1, cardY + 50, 4, 6, border);
        buf.FillRect(cardX + cardW - 1, cardY + 51, 3, 4, ColorAmberCardBg);

        buf.FillRect(cardX + 1, cardY + 1, cardW - 2, 16, ColorAmberHeaderBg);
        buf.FillRect(cardX + 1, cardY + 17, cardW - 2, 1, ColorAmber);

        buf.FillRect(cardX + 6, cardY + 4, 9, 9, ColorAmberBright);
        buf.FillRect(cardX + 7, cardY + 5, 7, 7, ColorAmberCardBg);
        PixelFont.DrawTextCentered(buf, cardX + 10, cardY + 6, "!", ColorAmberBright, PixelFontSize.Small);

        string shortName = ShortenName(agent.Name, agent.Id);
        int written = 0;
        ReadOnlySpan<char> snSpan = shortName.AsSpan();
        int toCopy = Math.Min(snSpan.Length, _textScratch.Length);
        snSpan.Slice(0, toCopy).CopyTo(_textScratch.AsSpan());
        written += toCopy;
        PixelFont.DrawTextTruncated(buf, cardX + 19, cardY + 5, cardW - 24, _textScratch.AsSpan(0, written), ColorWhite, PixelFontSize.Small);

        string prompt = !string.IsNullOrWhiteSpace(agent.QuestionPrompt)
            ? agent.QuestionPrompt!
            : (!string.IsNullOrWhiteSpace(agent.TaskTitle) ? agent.TaskTitle! : "Approval requested. Open Orca to respond.");

        PixelFont.DrawWrappedText(
            buf,
            cardX + 8,
            cardY + 22,
            cardW - 16,
            maxLines: 2,
            prompt,
            ColorUtil.FromRgb(254, 243, 199),
            PixelFontSize.Small,
            lineSpacing: 4);

        const int btnY = cardY + 51;
        const int btnH = 17;

        const int openX = cardX + 8;
        const int openW = 92;
        buf.DrawPixelCard(openX, btnY, openW, btnH, ColorUtil.FromRgb(217, 119, 6), ColorAmberBright, ColorUtil.WithAlpha(ColorWhite, 75));
        PixelFont.DrawTextCentered(buf, openX + openW / 2, btnY + 5, "OPEN ORCA", ColorOutline, PixelFontSize.Regular);
        _hitRegions.Add(new PetHitRegion("open_orca", openX, btnY, openW, btnH, "Open Orca"));

        const int dismissW = 24;
        const int dismissX = cardX + cardW - dismissW - 8;
        buf.DrawPixelCard(dismissX, btnY, dismissW, btnH, ColorUtil.FromRgb(56, 28, 36), ColorCrimson);
        PixelFont.DrawTextCentered(buf, dismissX + dismissW / 2, btnY + 5, "X", ColorUtil.FromRgb(254, 202, 202), PixelFontSize.Regular);
        _hitRegions.Add(new PetHitRegion("dismiss", dismissX, btnY, dismissW, btnH, "Dismiss", agent.Id, AgentState.NeedsApproval));
    }

    private void RenderFailedMode(PixelBuffer buf, AgentSnapshot agent, float timeSeconds, bool emitTick)
    {
        ActiveAlertAgentId = agent.Id;
        ActiveAlertAgentState = AgentState.Failed;

        const int charX = 344;
        const int laptopX = 314;
        string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(agent.SpriteKey);

        ShinobiSpriteRenderer.DrawBurningLaptop(buf, laptopX, GroundY, timeSeconds);
        ShinobiSpriteRenderer.DrawCharacter(buf, sprite, AgentState.Failed, charX, GroundY, timeSeconds, behaviorVariant: agent.BehaviorVariant);
        _hitRegions.Add(new PetHitRegion($"agent:{agent.Id}", charX - 16, GroundY - 30, 32, 32, agent.Name));

        if (emitTick)
        {
            _particles.EmitFireAndSmoke(laptopX, GroundY - 9);
        }

        const int cardX = 114;
        const int cardY = 66;
        const int cardW = 184;
        const int cardH = 54;
        int border = ((int)(timeSeconds * 6f) & 1) == 0 ? ColorCrimson : ColorUtil.FromRgb(252, 165, 165);

        buf.DrawPixelCard(cardX, cardY, cardW, cardH, ColorCrimsonCardBg, border);
        buf.FillRect(cardX + cardW - 1, cardY + 34, 4, 5, border);
        buf.FillRect(cardX + cardW - 1, cardY + 35, 3, 3, ColorCrimsonCardBg);

        string shortName = ShortenName(agent.Name, agent.Id);
        int written = 0;
        "FAIL \u2022 ".AsSpan().CopyTo(_textScratch.AsSpan());
        written += 7;
        ReadOnlySpan<char> snSpan = shortName.AsSpan();
        int toCopy = Math.Min(snSpan.Length, _textScratch.Length - written);
        snSpan.Slice(0, toCopy).CopyTo(_textScratch.AsSpan(written));
        written += toCopy;
        PixelFont.DrawTextTruncated(buf, cardX + 8, cardY + 7, cardW - 16, _textScratch.AsSpan(0, written), ColorCrimson, PixelFontSize.Regular);
        PixelFont.DrawTextTruncated(buf, cardX + 8, cardY + 20, cardW - 16, "FAIL \u2022 CLICK DETAILS", ColorUtil.FromRgb(254, 202, 202), PixelFontSize.Small);

        const int btnY = cardY + 32;
        buf.DrawPixelCard(cardX + 8, btnY, 82, 16, ColorUtil.FromRgb(185, 28, 28), ColorUtil.FromRgb(252, 165, 165));
        PixelFont.DrawTextCentered(buf, cardX + 49, btnY + 5, "OPEN ORCA", ColorWhite, PixelFontSize.Small);
        _hitRegions.Add(new PetHitRegion("open_orca", cardX + 8, btnY, 82, 16, "Open Orca"));

        int dismissX = cardX + cardW - 28;
        buf.DrawPixelCard(dismissX, btnY, 20, 16, ColorUtil.FromRgb(56, 28, 36), ColorCrimson);
        PixelFont.DrawTextCentered(buf, dismissX + 10, btnY + 5, "X", ColorWhite, PixelFontSize.Small);
        _hitRegions.Add(new PetHitRegion("dismiss", dismissX, btnY, 20, 16, "Dismiss", agent.Id, AgentState.Failed));
    }

    private void RenderDoneMode(PixelBuffer buf, AgentSnapshot agent, float timeSeconds, bool emitTick)
    {
        const int charX = 342;
        string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(agent.SpriteKey);

        ShinobiSpriteRenderer.DrawCharacter(buf, sprite, AgentState.Done, charX, GroundY, timeSeconds, behaviorVariant: agent.BehaviorVariant);
        _hitRegions.Add(new PetHitRegion($"agent:{agent.Id}", charX - 16, GroundY - 32, 32, 34, agent.Name));

        if (emitTick)
        {
            _particles.EmitConfetti(charX, GroundY - 26, 5);
        }

        const int cardX = 152;
        const int cardY = 78;
        const int cardW = 166;
        const int cardH = 42;
        buf.DrawPixelCard(cardX, cardY, cardW, cardH, ColorEmeraldCardBg, ColorEmerald);
        buf.FillRect(cardX + cardW - 1, cardY + 26, 4, 5, ColorEmerald);
        buf.FillRect(cardX + cardW - 1, cardY + 27, 3, 3, ColorEmeraldCardBg);

        PixelFont.DrawText(buf, cardX + 8, cardY + 8, "PASS", ColorEmerald, PixelFontSize.Regular);
        PixelFont.DrawTextTruncated(buf, cardX + 42, cardY + 10, cardW - 50, ShortenName(agent.Name, agent.Id), ColorWhite, PixelFontSize.Small);
        string msg = !string.IsNullOrWhiteSpace(agent.TaskTitle) ? agent.TaskTitle! : "TASK COMPLETE!";
        PixelFont.DrawTextTruncated(buf, cardX + 8, cardY + 24, cardW - 16, msg, ColorUtil.FromRgb(167, 243, 208), PixelFontSize.Small);

        _hitRegions.Add(new PetHitRegion("open_orca", cardX, cardY, cardW, cardH, "Open Orca"));
    }

    internal string? LastRenderedIdlePrimarySprite { get; private set; }

    private void RenderSquadCelebrationMode(
        PixelBuffer buf,
        HqState state,
        int count,
        float timeSeconds,
        bool emitTick,
        bool isNight,
        SceneEffectsState effects)
    {
        if (count <= 0)
        {
            count = Math.Min(4, state.Agents.Count);
            for (int i = 0; i < count; i++) _celebrateScratch[i] = state.Agents[i];
        }
        if (count <= 0) return;

        int spacing = count == 2 ? 64 : 46;
        const int rightmostX = 346;
        int minX = Math.Max(12, rightmostX - (count - 1) * spacing - 24);
        bool celebrationActive = _dramaController.IsCelebrationActive;

        if (celebrationActive && emitTick && _particles.ActiveCount < 48)
        {
            _particles.EmitCelebrationConfettiShower(minX, CanvasWidth - 10, 16, 8);
        }

        for (int i = 0; i < count; i++)
        {
            AgentSnapshot ag = _celebrateScratch[i];
            int cx = rightmostX - i * spacing;
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);

            if (isNight)
            {
                buf.BlendRect(cx - 14, GroundY - 5, 28, 4, ColorGold, 30);
            }

            if (effects.IsMediaPlaying)
            {
                ShinobiSpriteRenderer.DrawMusicEqualizer(buf, cx - 22, GroundY - 9, timeSeconds, seed: i);
            }

            ShinobiSpriteRenderer.DrawCharacter(
                buf,
                sprite,
                AgentState.Done,
                cx,
                GroundY,
                timeSeconds,
                phaseOffset: i,
                behaviorVariant: ag.BehaviorVariant,
                celebrationHop: celebrationActive);

            int hitCx = Math.Clamp(cx, 18, CanvasWidth - 18);
            if (!string.Equals(_cachedCelebrateAgentIds[i], ag.Id, StringComparison.OrdinalIgnoreCase))
            {
                _cachedCelebrateAgentIds[i] = ag.Id;
                _cachedCelebrateActionIds[i] = $"agent:{ag.Id}";
            }
            _hitRegions.Add(new PetHitRegion(_cachedCelebrateActionIds[i]!, hitCx - 16, GroundY - 34, 32, 36, ag.Name));
        }

        // Crisp celebratory emerald/gold banner pill above the squad
        ReadOnlySpan<char> banner = celebrationActive ? "SQUAD CELEBRATION!".AsSpan() : "TASKS COMPLETE!".AsSpan();
        int pillW = Math.Min(150, PixelFont.MeasureWidth(banner, PixelFontSize.Small) + 22);
        int pillH = 17;
        int groupCenterX = rightmostX - ((count - 1) * spacing) / 2;
        int pillX = Math.Clamp(groupCenterX - pillW / 2, 8, CanvasWidth - pillW - 6);
        int pillY = GroundY - 56;
        int borderCol = celebrationActive && (((int)(timeSeconds * 8f)) & 1) == 0 ? ColorGold : ColorEmerald;

        buf.BlendRect(pillX + 1, pillY + 1, pillW, pillH, ColorOutline, 135);
        buf.DrawPixelCard(pillX, pillY, pillW, pillH, ColorEmeraldCardBg, borderCol, ColorUtil.WithAlpha(ColorWhite, 42));
        buf.FillRect(pillX + 6, pillY + 6, 4, 4, ColorGold);
        PixelFont.DrawTextTruncated(buf, pillX + 14, pillY + 6, pillW - 18, banner, ColorWhite, PixelFontSize.Small);
        _hitRegions.Add(new PetHitRegion("open_orca", pillX, pillY, pillW, pillH, "Open Orca"));
    }

    private void RenderWorkingMode(
        PixelBuffer buf,
        HqState state,
        int stripCount,
        int totalWorking,
        float dt,
        float timeSeconds,
        bool emitTick,
        bool consumeNewHandoffs,
        bool isNight,
        SceneEffectsState effects)
    {
        int spacing = stripCount == 2 ? 68 : (stripCount <= 4 ? 48 : 38);
        const int rightmostX = 346;
        const int deskCharY = GroundY - 6;

        // 1. Update ActorMotionSystem with already-built live strip slot bindings
        _motionSystem.Update(
            dt,
            timeSeconds,
            state,
            _slotBindings.AsSpan(0, stripCount),
            coreDeskY: deskCharY,
            extraDeskY: deskCharY,
            particles: _particles,
            rugOffsetY: 4,
            enableAmbientStroll: false,
            consumeNewHandoffs: consumeNewHandoffs);

        // 2. Pass 1: Stationary / behind-desk characters + ALL mini-desks
        for (int i = 0; i < stripCount; i++)
        {
            AgentSnapshot ag = _workingScratch[i];
            int cx = rightmostX - i * spacing;
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);
            AgentActionKind actionKind = _cachedWorkingItems[i].ActionKind;
            ref readonly var motion = ref _motionSystem.GetMotion(i);
            bool panicSprint = _dramaController.IsSlotInPanicSprint(ag.Id, sprite);

            if (motion.BehindDesk)
            {
                bool isStepping = motion.State is ActorMotionState.StepOut or ActorMotionState.StepBack;
                ShinobiSpriteRenderer.DrawCharacter(
                    buf,
                    sprite,
                    ag.State == AgentState.Idle ? AgentState.Working : ag.State,
                    cx + motion.OffsetX,
                    deskCharY + motion.OffsetY,
                    timeSeconds,
                    phaseOffset: i,
                    behindDesk: true,
                    actionKind: actionKind,
                    isWalking: isStepping,
                    carryingScroll: motion.CarryingScroll,
                    facingRight: motion.FacingRight,
                    behaviorVariant: ag.BehaviorVariant,
                    panicSprint: panicSprint,
                    celebrationHop: _dramaController.IsCelebrationActive,
                    musicGroove: effects.IsMediaPlaying && !panicSprint);
            }

            ShinobiSpriteRenderer.DrawMiniTaskbarDesk(
                buf,
                cx,
                GroundY,
                _palette.DeskTop,
                ag.State == AgentState.Idle ? AgentState.Idle : AgentState.Working,
                timeSeconds,
                actionKind: actionKind,
                deskRig: ag.DeskRig,
                behaviorVariant: ag.BehaviorVariant);

            if (isNight)
            {
                ShinobiSpriteRenderer.DrawNightDeskLamp(buf, cx, GroundY, timeSeconds, miniDesk: true);
            }

            if (effects.IsMediaPlaying)
            {
                ShinobiSpriteRenderer.DrawMusicEqualizer(buf, cx - 18, GroundY - 12, timeSeconds, seed: i);
            }

            if (motion.BehindDesk)
            {
                int hitCx = Math.Clamp(cx, 18, CanvasWidth - 18);
                _hitRegions.Add(new PetHitRegion(_cachedWorkingItems[i].HitRegionAction, hitCx - 18, GroundY - 36, 36, 38, ag.Name));
            }

            if (emitTick && !motion.IsInMotion && ag.State == AgentState.Working)
            {
                if (sprite == "minato") _particles.EmitSparks(cx, GroundY - 20, 2);
                else if (sprite == "tsunade") _particles.EmitChakraBubble(cx + 8, GroundY - 13);
            }
        }

        // 3. Pass 2: Walking / Delivering foreground actors + Minato golden Hiraishin afterimages
        for (int i = 0; i < stripCount; i++)
        {
            ref readonly var motion = ref _motionSystem.GetMotion(i);
            if (motion.BehindDesk) continue;

            AgentSnapshot ag = _workingScratch[i];
            int cx = rightmostX - i * spacing;
            int actorX = cx + motion.OffsetX;
            int actorY = deskCharY + motion.OffsetY;
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);
            AgentActionKind actionKind = _cachedWorkingItems[i].ActionKind;
            bool panicSprint = _dramaController.IsSlotInPanicSprint(ag.Id, sprite);

            if (motion.IsFlashTrail && motion.State is ActorMotionState.WalkingToTarget or ActorMotionState.WalkingBack)
            {
                int trailDir = motion.FacingRight ? -1 : 1;
                DrawHiraishinAfterimage(buf, actorX + trailDir * 16, actorY, 58);
                DrawHiraishinAfterimage(buf, actorX + trailDir * 8, actorY, 118);
                buf.BlendRect(actorX + trailDir * 14 - 4, actorY - 14, 10, 1, ColorAmberBright, 165);
                buf.BlendRect(actorX + trailDir * 10 - 3, actorY - 9, 8, 1, ColorGold, 190);
            }

            bool isWalking = motion.State is ActorMotionState.StepOut or ActorMotionState.WalkingToTarget or ActorMotionState.WalkingBack or ActorMotionState.StepBack;
            ShinobiSpriteRenderer.DrawCharacter(
                buf,
                sprite,
                ag.State == AgentState.Idle ? AgentState.Working : ag.State,
                actorX,
                actorY,
                timeSeconds,
                phaseOffset: i,
                behindDesk: false,
                actionKind: actionKind,
                isWalking: isWalking,
                carryingScroll: motion.CarryingScroll,
                facingRight: motion.FacingRight,
                behaviorVariant: ag.BehaviorVariant,
                panicSprint: panicSprint,
                celebrationHop: _dramaController.IsCelebrationActive,
                musicGroove: effects.IsMediaPlaying && !panicSprint);

            if (motion.State == ActorMotionState.Delivering)
            {
                int scrollSparkX = actorX + (motion.FacingRight ? 7 : -7);
                int scrollSparkY = actorY - 11;
                DrawDeliverySparklePop(buf, scrollSparkX, scrollSparkY, timeSeconds);
            }

            int hitCx = Math.Clamp(actorX, 18, CanvasWidth - 18);
            _hitRegions.Add(new PetHitRegion(_cachedWorkingItems[i].HitRegionAction, hitCx - 18, GroundY - 36, 36, 38, ag.Name));
        }

        // 4. Status pill (crimson drama pill, gold handoff pill, or cyan working pill)
        bool dramaActive = _dramaController.IsDramaActive;
        bool handoffActive = _motionSystem.IsHandoffActive;
        ReadOnlySpan<char> pillText;
        if (dramaActive)
        {
            pillText = (_dramaController.IsRedScrollFlying ? "REQUEST CHANGES!" : "PANIC SPRINT • FIX PR!").AsSpan();
        }
        else if (handoffActive)
        {
            int fromIdx = _motionSystem.ActiveHandoffStationIndex;
            string fromName = (fromIdx >= 0 && fromIdx < stripCount && !string.IsNullOrEmpty(_cachedWorkingItems[fromIdx].ShortName))
                ? _cachedWorkingItems[fromIdx].ShortName
                : "AGENT";
            int written = 0;
            "HANDOFF \u2022 ".AsSpan().CopyTo(_textScratch.AsSpan());
            written += 10;
            ReadOnlySpan<char> fn = fromName.AsSpan();
            int c1 = Math.Min(fn.Length, _textScratch.Length - written);
            fn.Slice(0, c1).CopyTo(_textScratch.AsSpan(written));
            written += c1;
            pillText = _textScratch.AsSpan(0, written);
        }
        else if (stripCount == 1)
        {
            AgentSnapshot a = _workingScratch[0];
            string shortName = _cachedSingleWorkingShortName ?? _cachedWorkingItems[0].ShortName ?? "AGENT";
            string shortLabel = _cachedWorkingItems[0].ShortLabel;
            int written = 0;
            ReadOnlySpan<char> sn = shortName.AsSpan();
            int c1 = Math.Min(sn.Length, _textScratch.Length);
            sn.Slice(0, c1).CopyTo(_textScratch.AsSpan());
            written += c1;
            if (written + 3 <= _textScratch.Length)
            {
                " \u2022 ".AsSpan().CopyTo(_textScratch.AsSpan(written));
                written += 3;
            }
            ReadOnlySpan<char> t = shortLabel.AsSpan();
            int c2 = Math.Min(t.Length, _textScratch.Length - written);
            t.Slice(0, c2).CopyTo(_textScratch.AsSpan(written));
            written += c2;

            if (a.WorkStartedAt.HasValue)
            {
                var now = _stateTimestamp;
                var elapsed = now - a.WorkStartedAt.Value;
                long totalSeconds = Math.Clamp((long)elapsed.TotalSeconds, 0L, 5999L);
                int mm = (int)(totalSeconds / 60);
                int ss = (int)(totalSeconds % 60);
                if (written + 8 <= _textScratch.Length)
                {
                    " \u2022 ".AsSpan().CopyTo(_textScratch.AsSpan(written));
                    written += 3;
                    _textScratch[written++] = (char)('0' + (mm / 10));
                    _textScratch[written++] = (char)('0' + (mm % 10));
                    _textScratch[written++] = ':';
                    _textScratch[written++] = (char)('0' + (ss / 10));
                    _textScratch[written++] = (char)('0' + (ss % 10));
                }
            }

            pillText = _textScratch.AsSpan(0, written);
        }
        else
        {
            string label = _cachedWorkingMultiLabel ?? (totalWorking > 6
                ? $"{stripCount} WORKING (+{totalWorking - stripCount})"
                : $"{totalWorking} AGENTS WORKING");
            pillText = label.AsSpan();
        }

        int textW = PixelFont.MeasureWidth(pillText, PixelFontSize.Small);
        int pillW = Math.Min(172, Math.Max(78, textW + 24));
        int pillH = 18;
        int groupCenterX = rightmostX - ((stripCount - 1) * spacing) / 2;
        int pillX = Math.Clamp(groupCenterX - pillW / 2, 8, CanvasWidth - pillW - 6);
        int pillY = dramaActive ? GroundY - 76 : GroundY - 58;

        int pillBg = dramaActive ? ColorCrimsonCardBg : (handoffActive ? ColorAmberCardBg : ColorCyanCardBg);
        int pillBorder = dramaActive ? ColorCrimson : (handoffActive ? ColorAmberBright : ColorCyan);
        int ledCol = dramaActive ? ColorCrimson : (handoffActive ? ColorGold : ColorCyan);

        buf.BlendRect(pillX + 1, pillY + 1, pillW, pillH, ColorOutline, 130);
        buf.DrawPixelCard(pillX, pillY, pillW, pillH, pillBg, pillBorder, ColorUtil.WithAlpha(ColorWhite, 40));

        int ledAlpha = 170 + (int)(MathF.Sin(timeSeconds * 6f) * 75f);
        buf.BlendRect(pillX + 7, pillY + 7, 4, 4, ledCol, ledAlpha);
        PixelFont.DrawTextTruncated(buf, pillX + 15, pillY + 6, pillW - 21, pillText, ColorWhite, PixelFontSize.Small);

        bool singleProcess = !handoffActive && !dramaActive && stripCount == 1 &&
            string.Equals(_workingScratch[0].Source, "process", StringComparison.OrdinalIgnoreCase);
        string actionId = singleProcess
            ? (_cachedSingleWorkingFocusActionId ?? (HasVerifiedProcessIdentity(_workingScratch[0])
                ? $"focus_agent:{_workingScratch[0].Id}"
                : $"agent:{_workingScratch[0].Id}"))
            : "open_orca";
        string actionLabel = singleProcess
            ? _workingScratch[0].Name
            : "Open Orca";

        _hitRegions.Add(new PetHitRegion(actionId, pillX, pillY, pillW, pillH, actionLabel));
    }

    private static void DrawHiraishinAfterimage(PixelBuffer buf, int cx, int bottomY, int alpha)
    {
        buf.BlendRect(cx - 4, bottomY - 22, 8, 8, ColorAmberBright, alpha);
        buf.BlendRect(cx - 2, bottomY - 24, 4, 2, ColorGold, alpha);
        buf.BlendRect(cx - 6, bottomY - 14, 12, 9, ColorGold, alpha);
        buf.BlendRect(cx - 5, bottomY - 7, 10, 2, ColorAmber, alpha);
        buf.BlendRect(cx - 4, bottomY - 5, 3, 4, ColorGold, alpha);
        buf.BlendRect(cx + 1, bottomY - 5, 3, 4, ColorGold, alpha);
    }

    private static void DrawDeliverySparklePop(PixelBuffer buf, int cx, int cy, float timeSeconds)
    {
        int pulse = ((int)(timeSeconds * 12f) & 1) == 0 ? 1 : 0;
        int r = 4 + pulse;
        buf.BlendRect(cx - r, cy, r * 2 + 1, 1, ColorAmberBright, 220);
        buf.BlendRect(cx, cy - r, 1, r * 2 + 1, ColorAmberBright, 220);
        buf.BlendRect(cx - 2, cy - 2, 5, 5, ColorGold, 130);
        buf.SetPixel(cx, cy, ColorWhite);
    }

    private void RenderMixiChillMode(PixelBuffer buf, float timeSeconds, bool isNight, SceneEffectsState effects)
    {
        const int mixiCx = 330;
        const int mixiGroundY = GroundY - 2; // 122 -> spans y = 76..122, x = 290..368 (zero clipping!)

        MixiChillActivity activity = MixiChillRenderer.DrawChillCorner(
            buf,
            mixiCx,
            mixiGroundY,
            timeSeconds,
            activityOverride: effects.PinnedMixiActivity,
            isStartled: effects.IsMixiStartled,
            isMediaPlaying: effects.IsMediaPlaying,
            isNightMode: isNight);
        IsMixiChillActive = true;
        ActiveMixiActivity = activity;

        // Clean compact badge above the chill corner: DO MIXI · AN MI / YOUTUBE / GAME / STREAM / KARAOKE
        string badgeText = MixiChillRenderer.GetFullBadge(activity);
        int textW = PixelFont.MeasureWidth(badgeText.AsSpan(), PixelFontSize.Small);
        int pillW = Math.Min(134, Math.Max(82, textW + 20));
        const int pillH = 16;
        int pillX = Math.Clamp(mixiCx - pillW / 2, 8, CanvasWidth - pillW - 6);
        const int pillY = 52;

        buf.BlendRect(pillX + 1, pillY + 1, pillW, pillH, ColorOutline, 135);
        buf.DrawPixelCard(pillX, pillY, pillW, pillH, ColorCrimsonCardBg, ColorCrimson, ColorUtil.WithAlpha(ColorWhite, 38));

        int dotAlpha = 180 + (int)(MathF.Sin(timeSeconds * 5f) * 65f);
        buf.BlendRect(pillX + 6, pillY + 6, 4, 4, ColorAmberBright, dotAlpha);
        PixelFont.DrawTextTruncated(buf, pillX + 13, pillY + 5, pillW - 18, badgeText, ColorWhite, PixelFontSize.Small);

        // Interactive poke speech bubble rendered cleanly above badge (y = 30..48 -> zero clipping!)
        if (effects.HasActiveMixiSpeech)
        {
            MixiChillRenderer.DrawPokeSpeechBubble(
                buf,
                anchorX: 342,
                anchorY: 48,
                effects.MixiSpeechText.AsSpan(),
                effects.MixiPokeElapsedSeconds,
                CanvasWidth,
                CanvasHeight);
        }

        // Clickable hit region covering the Do Mixi Gaming chill corner & badge
        int hitLeft = Math.Min(pillX, mixiCx - 40);
        int hitRight = Math.Min(CanvasWidth, Math.Max(pillX + pillW, mixiCx + 38));
        _hitRegions.Add(new PetHitRegion("agent:mixi-chill", hitLeft, pillY, hitRight - hitLeft, mixiGroundY - pillY + 2, badgeText));
    }

    private void RenderIdleChillMode(
        PixelBuffer buf,
        HqState state,
        float timeSeconds,
        bool emitTick,
        bool isNight,
        SceneEffectsState effects)
    {
        const int primaryX = 346;

        string primarySprite = _idlePrimaryNonProcess != null
            ? ShinobiSpriteRenderer.NormalizeSpriteKey(_idlePrimaryNonProcess.SpriteKey)
            : "kakashi";
        string primaryName = _idlePrimaryNonProcess?.Name ?? primarySprite;
        string primaryActionId = _cachedIdlePrimaryActionId ?? $"agent:{primarySprite}";
        int primaryVariant = _idlePrimaryNonProcess?.BehaviorVariant ?? 0;

        LastRenderedIdlePrimarySprite = primarySprite;

        if (isNight)
        {
            buf.BlendRect(primaryX - 18, GroundY - 5, 36, 4, ColorGold, 30);
        }

        ShinobiSpriteRenderer.DrawCharacter(
            buf,
            primarySprite,
            AgentState.Idle,
            primaryX,
            GroundY,
            timeSeconds,
            behaviorVariant: primaryVariant,
            celebrationHop: _dramaController.IsCelebrationActive,
            musicGroove: effects.IsMediaPlaying);
        _hitRegions.Add(new PetHitRegion(primaryActionId, primaryX - 16, GroundY - 30, 32, 32, primaryName));

        if (effects.IsMediaPlaying)
        {
            ShinobiSpriteRenderer.DrawMusicEqualizer(buf, primaryX - 24, GroundY - 10, timeSeconds, seed: 3);
        }

        if (primarySprite == "shikamaru")
        {
            ShinobiSpriteRenderer.DrawCoffeeCup(buf, primaryX - 15, GroundY - 5, timeSeconds);
            if (emitTick) _particles.EmitCoffeeSteam(primaryX - 13, GroundY - 6);
        }

        // Draw up to 2 chilling idle terminal agents to the left
        for (int i = 0; i < _idleTerminalCount; i++)
        {
            var tAg = _idleScratch[i];
            int tx = primaryX - (i + 1) * 38;
            string tSprite = ShinobiSpriteRenderer.NormalizeSpriteKey(tAg.SpriteKey);
            string actionId = _cachedIdleTerminalActionIds[i] ?? $"agent:{tAg.Id}";

            if (isNight)
            {
                buf.BlendRect(tx - 14, GroundY - 5, 28, 4, ColorGold, 26);
            }

            ShinobiSpriteRenderer.DrawCharacter(
                buf,
                tSprite,
                AgentState.Idle,
                tx,
                GroundY,
                timeSeconds,
                phaseOffset: i + 1,
                behaviorVariant: tAg.BehaviorVariant,
                celebrationHop: _dramaController.IsCelebrationActive,
                musicGroove: effects.IsMediaPlaying);
            _hitRegions.Add(new PetHitRegion(actionId, tx - 16, GroundY - 30, 32, 32, tAg.Name));
        }

        // Chill bubble
        int leftmostCharX = primaryX - _idleTerminalCount * 38;
        int bW = _idleTerminalCount > 0 ? 84 : 76;
        int bX = Math.Max(8, leftmostCharX - bW - 12);
        const int bY = 92;
        const int bH = 22;

        buf.DrawPixelCard(bX, bY, bW, bH, ColorUtil.FromRgb(22, 26, 38), ColorSlate);

        if (_idleTerminalCount > 0)
        {
            string topText = _cachedIdleTopText ?? (_idleTerminalCount == 1 ? $"{ShortenName(_idleScratch[0].Name, _idleScratch[0].Id)} READY" : $"{_idleTerminalCount} TERMINALS");
            PixelFont.DrawTextCentered(buf, bX + bW / 2, bY + 5, topText, ColorUtil.FromRgb(203, 213, 225), PixelFontSize.Small);
            PixelFont.DrawTextCentered(buf, bX + bW / 2, bY + 13, "HQ IDLE", ColorSlate, PixelFontSize.Small);
        }
        else
        {
            string idleCaption = primarySprite switch
            {
                "kakashi" => "READING..",
                "shikamaru" => "COFFEE..",
                "minato" => "STANDBY..",
                "tsunade" => "RESTING..",
                _ => "CHILLING.."
            };
            PixelFont.DrawTextCentered(buf, bX + bW / 2, bY + 5, idleCaption, ColorUtil.FromRgb(203, 213, 225), PixelFontSize.Small);
            PixelFont.DrawTextCentered(buf, bX + bW / 2, bY + 13, "HQ IDLE", ColorSlate, PixelFontSize.Small);
        }
    }

    private static void DrawWakeUpPulse(PixelBuffer buf, float timeSeconds, float remainingSeconds)
    {
        if (remainingSeconds <= 0f) return;

        int pulseAlpha = 140 + (int)(MathF.Sin(timeSeconds * 10f) * 90f);
        int ringCol = (((int)(timeSeconds * 8f)) & 1) == 0 ? ColorGold : ColorCyan;
        int col = ColorUtil.WithAlpha(ringCol, Math.Clamp(pulseAlpha, 60, 240));
        // Clean glowing highlight pill around the right taskbar pet dock area
        buf.DrawOutlineRect(274, 46, 106, 80, col, thickness: 2);
    }

    private static string ShortenName(string name, string? id = null)
    {
        string clean = AgentAppearanceRegistry.SanitizeDisplayName(name, id);
        int idx = clean.IndexOf('(');
        if (idx > 0) clean = clean[..idx];
        return clean.Trim();
    }
}
