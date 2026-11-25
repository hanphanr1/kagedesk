using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Sprites;

namespace KageDesk.Rendering.Scenes;

public sealed record PetHitRegion(
    string ActionId,
    int X,
    int Y,
    int Width,
    int Height,
    string? Label = null
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
    private readonly SlotBinding[] _slotBindings = new SlotBinding[6];
    private readonly AgentSnapshot[] _workingScratch = new AgentSnapshot[6];
    private readonly AgentSnapshot[] _idleScratch = new AgentSnapshot[2];
    private int _idleTerminalCount = 0;
    private AgentSnapshot? _idlePrimaryNonProcess = null;
    private readonly char[] _textScratch = new char[64];

    private struct WorkingCacheItem
    {
        public AgentActionKind ActionKind;
        public string ShortLabel;
        public string HitRegionAction;
    }

    private readonly WorkingCacheItem[] _cachedWorkingItems = new WorkingCacheItem[6];
    private string? _cachedSingleWorkingFocusActionId;
    private string? _cachedSingleWorkingShortName;
    private string? _cachedIdlePrimaryActionId;
    private readonly string?[] _cachedIdleTerminalActionIds = new string?[2];
    private string? _cachedIdleTopText;
    private string? _cachedWorkingMultiLabel;
    private HqState? _lastRenderedState;
    private int _lastStripCount;

    private ThemePack? _cachedTheme;
    private ResolvedScenePalette _palette;
    private float _lastTime = -1f;
    private float _emitAccum;
    private DateTimeOffset _stateTimestamp;

    public IReadOnlyList<PetHitRegion> HitRegions => _hitRegions;
    public bool IsHandoffActive => _motionSystem.IsHandoffActive;
    public ActorMotionSystem MotionSystem => _motionSystem;
    public bool IsMixiChillActive { get; private set; }
    public MixiChillActivity? ActiveMixiActivity { get; private set; }

    public void ResetEffects()
    {
        _particles.Reset();
        _motionSystem.Reset();
        _lastTime = -1f;
        _emitAccum = 0f;
        _lastRenderedState = null;
        _lastStripCount = 0;
        IsMixiChillActive = false;
        ActiveMixiActivity = null;
    }

    public IReadOnlyList<PetHitRegion> Render(
        PixelBuffer buf,
        HqState state,
        ThemePack theme,
        float timeSeconds,
        bool showIdleChill = true,
        DateTimeOffset? nowOverride = null,
        bool showMixiWhenAllIdle = true)
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

        buf.ClearTransparent();
        _hitRegions.Clear();
        IsMixiChillActive = false;
        ActiveMixiActivity = null;

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
            RenderMixiChillMode(buf, timeSeconds);
            _particles.Update(dt);
            _particles.Render(buf);
            return _hitRegions;
        }

        AgentSnapshot? approvalAgent = null;
        AgentSnapshot? failedAgent = null;
        AgentSnapshot? doneAgent = null;
        int workingCount = 0;
        int totalWorking = 0;

        var agents = state.Agents;
        for (int i = 0; i < agents.Count; i++)
        {
            AgentSnapshot ag = agents[i];
            if (ag.State == AgentState.NeedsApproval && approvalAgent == null)
            {
                approvalAgent = ag;
            }
            else if (ag.State == AgentState.Failed && failedAgent == null)
            {
                failedAgent = ag;
            }
            else if (ag.State == AgentState.Done && doneAgent == null)
            {
                doneAgent = ag;
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

        // Resolve active or newly pending handoff endpoints so BOTH sender and recipient
        // are guaranteed visible in the Taskbar Pet strip (up to 6 slots).
        AgentSnapshot? handoffFrom = null;
        AgentSnapshot? handoffTo = null;
        bool hasNewResolvableHandoff = false;

        if (_motionSystem.IsHandoffActive)
        {
            handoffFrom = FindExactAgentById(agents, _motionSystem.ActiveHandoffFromAgentId);
            handoffTo = FindExactAgentById(agents, _motionSystem.ActiveHandoffToAgentId);
        }
        else if (_motionSystem.TryGetPendingHandoffEvent(state, out var pendingHandoff))
        {
            handoffFrom = ResolveHandoffAgentInSnapshot(pendingHandoff.AgentId, agents, pendingHandoff.FromIsConcreteId);
            handoffTo = ResolveHandoffAgentInSnapshot(pendingHandoff.TargetAgentId, agents, pendingHandoff.ToIsConcreteId);
            if (handoffFrom != null && handoffTo != null &&
                !string.Equals(handoffFrom.Id, handoffTo.Id, StringComparison.OrdinalIgnoreCase))
            {
                hasNewResolvableHandoff = true;
            }
        }

        bool handoffMode = _motionSystem.IsHandoffActive || hasNewResolvableHandoff;
        int stripCount = workingCount;

        if (handoffMode && approvalAgent == null && failedAgent == null)
        {
            EnsureAgentInStrip(handoffFrom, handoffTo, ref stripCount);
            EnsureAgentInStrip(handoffTo, handoffFrom, ref stripCount);

            // Preserve the active handoff sender's slot index while motion is in flight so
            // ActorMotionSystem never cancels a valid handoff due to strip re-ordering.
            if (_motionSystem.IsHandoffActive && handoffFrom != null)
            {
                int pinnedIdx = _motionSystem.ActiveHandoffStationIndex;
                if (pinnedIdx >= 0 && pinnedIdx < stripCount)
                {
                    int currentIdx = FindInStrip(handoffFrom.Id, stripCount);
                    if (currentIdx >= 0 && currentIdx != pinnedIdx)
                    {
                        (_workingScratch[pinnedIdx], _workingScratch[currentIdx]) =
                            (_workingScratch[currentIdx], _workingScratch[pinnedIdx]);
                    }
                }
            }
        }

        bool stateOrStripChanged = !ReferenceEquals(state, _lastRenderedState) || stripCount != _lastStripCount;
        if (stateOrStripChanged)
        {
            _lastRenderedState = state;
            _lastStripCount = stripCount;
            RefreshStripCaches(agents, stripCount, totalWorking);
        }

        if (approvalAgent != null)
        {
            RenderApprovalMode(buf, approvalAgent, timeSeconds);
        }
        else if (failedAgent != null)
        {
            RenderFailedMode(buf, failedAgent, timeSeconds, emitTick);
        }
        else if (handoffMode && stripCount > 0)
        {
            RenderWorkingMode(buf, state, stripCount, Math.Max(totalWorking, stripCount), dt, timeSeconds, emitTick);
        }
        else if (doneAgent != null)
        {
            RenderDoneMode(buf, doneAgent, timeSeconds, emitTick);
        }
        else if (stripCount > 0)
        {
            RenderWorkingMode(buf, state, stripCount, totalWorking, dt, timeSeconds, emitTick);
        }
        else if (showIdleChill)
        {
            if (MixiChillRenderer.ShouldRenderMixiChillCorner(state, showMixiWhenAllIdle, _motionSystem.IsHandoffActive))
            {
                RenderMixiChillMode(buf, timeSeconds);
            }
            else
            {
                RenderIdleChillMode(buf, state, timeSeconds, emitTick);
            }
        }

        _particles.Update(dt);
        _particles.Render(buf);

        return _hitRegions;
    }

    private void RefreshStripCaches(IReadOnlyList<AgentSnapshot> agents, int stripCount, int totalWorking)
    {
        for (int i = 0; i < stripCount; i++)
        {
            var ag = _workingScratch[i];
            var (kind, label) = AgentActionClassifier.Classify(ag.State, ag.ToolName, ag.TaskTitle);
            _cachedWorkingItems[i] = new WorkingCacheItem
            {
                ActionKind = kind,
                ShortLabel = label,
                HitRegionAction = $"agent:{ag.Id}"
            };
        }

        if (stripCount == 1)
        {
            _cachedSingleWorkingShortName = ShortenName(_workingScratch[0].Name, _workingScratch[0].Id);
            _cachedSingleWorkingFocusActionId = string.Equals(_workingScratch[0].Source, "process", StringComparison.OrdinalIgnoreCase)
                ? $"focus_agent:{_workingScratch[0].Id}"
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
        _hitRegions.Add(new PetHitRegion("dismiss", dismissX, btnY, dismissW, btnH, "Dismiss"));
    }

    private void RenderFailedMode(PixelBuffer buf, AgentSnapshot agent, float timeSeconds, bool emitTick)
    {
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
        _hitRegions.Add(new PetHitRegion("dismiss", dismissX, btnY, 20, 16, "Dismiss"));
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

    private void RenderWorkingMode(
        PixelBuffer buf,
        HqState state,
        int stripCount,
        int totalWorking,
        float dt,
        float timeSeconds,
        bool emitTick)
    {
        int spacing = stripCount == 2 ? 68 : (stripCount <= 4 ? 48 : 38);
        const int rightmostX = 346;
        const int deskCharY = GroundY - 6;

        // 1. Bind visible slots into ActorMotionSystem (rugOffsetY: 4 keeps feet at GroundY-2=122, zero clipping!)
        for (int i = 0; i < stripCount; i++)
        {
            AgentSnapshot ag = _workingScratch[i];
            int cx = rightmostX - i * spacing;
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);
            _slotBindings[i] = new SlotBinding(i, ag.Id, sprite, ag.State, occupied: true, centerX: cx, deskY: deskCharY);
        }

        _motionSystem.Update(
            dt,
            timeSeconds,
            state,
            _slotBindings.AsSpan(0, stripCount),
            coreDeskY: deskCharY,
            extraDeskY: deskCharY,
            particles: _particles,
            rugOffsetY: 4,
            enableAmbientStroll: false);

        // 2. Pass 1: Stationary / behind-desk characters + ALL mini-desks (sender desk stays intact while away!)
        for (int i = 0; i < stripCount; i++)
        {
            AgentSnapshot ag = _workingScratch[i];
            int cx = rightmostX - i * spacing;
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);
            AgentActionKind actionKind = _cachedWorkingItems[i].ActionKind;
            ref readonly var motion = ref _motionSystem.GetMotion(i);

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
                    behaviorVariant: ag.BehaviorVariant);
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

            int hitCx = Math.Clamp(cx + (motion.BehindDesk ? 0 : motion.OffsetX), 18, CanvasWidth - 18);
            _hitRegions.Add(new PetHitRegion(_cachedWorkingItems[i].HitRegionAction, hitCx - 18, GroundY - 36, 36, 38, ag.Name));

            if (emitTick && !motion.IsInMotion && ag.State == AgentState.Working)
            {
                if (sprite == "minato") _particles.EmitSparks(cx, GroundY - 20, 2);
                else if (sprite == "tsunade") _particles.EmitChakraBubble(cx + 8, GroundY - 13);
            }
        }

        // 3. Pass 2: Walking / Delivering foreground actors + Minato golden Hiraishin afterimages + delivery sparkle
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

            if (motion.IsFlashTrail && motion.State is ActorMotionState.WalkingToTarget or ActorMotionState.WalkingBack)
            {
                int trailDir = motion.FacingRight ? -1 : 1;
                DrawHiraishinAfterimage(buf, actorX + trailDir * 16, actorY, 58);
                DrawHiraishinAfterimage(buf, actorX + trailDir * 8, actorY, 118);
                // Crisp golden Hiraishin horizontal speed streaks
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
                behaviorVariant: ag.BehaviorVariant);

            if (motion.State == ActorMotionState.Delivering)
            {
                int scrollSparkX = actorX + (motion.FacingRight ? 7 : -7);
                int scrollSparkY = actorY - 11;
                DrawDeliverySparklePop(buf, scrollSparkX, scrollSparkY, timeSeconds);
            }
        }

        // 4. Status pill (gold accent when a living handoff is active, else cyan working pill)
        bool handoffActive = _motionSystem.IsHandoffActive;
        ReadOnlySpan<char> pillText;
        if (handoffActive)
        {
            int fromIdx = _motionSystem.ActiveHandoffStationIndex;
            string fromName = (fromIdx >= 0 && fromIdx < stripCount)
                ? ShortenName(_workingScratch[fromIdx].Name, _workingScratch[fromIdx].Id)
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
            string shortName = _cachedSingleWorkingShortName ?? ShortenName(a.Name, a.Id);
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
        int pillY = GroundY - 58;

        int pillBg = handoffActive ? ColorAmberCardBg : ColorCyanCardBg;
        int pillBorder = handoffActive ? ColorAmberBright : ColorCyan;
        int ledCol = handoffActive ? ColorGold : ColorCyan;

        buf.BlendRect(pillX + 1, pillY + 1, pillW, pillH, ColorOutline, 130);
        buf.DrawPixelCard(pillX, pillY, pillW, pillH, pillBg, pillBorder, ColorUtil.WithAlpha(ColorWhite, 40));

        int ledAlpha = 170 + (int)(MathF.Sin(timeSeconds * 6f) * 75f);
        buf.BlendRect(pillX + 7, pillY + 7, 4, 4, ledCol, ledAlpha);
        PixelFont.DrawTextTruncated(buf, pillX + 15, pillY + 6, pillW - 21, pillText, ColorWhite, PixelFontSize.Small);

        string actionId = (!handoffActive && stripCount == 1 && string.Equals(_workingScratch[0].Source, "process", StringComparison.OrdinalIgnoreCase))
            ? (_cachedSingleWorkingFocusActionId ?? $"focus_agent:{_workingScratch[0].Id}")
            : "open_orca";
        string actionLabel = (!handoffActive && stripCount == 1 && string.Equals(_workingScratch[0].Source, "process", StringComparison.OrdinalIgnoreCase))
            ? _workingScratch[0].Name
            : "Open Orca";

        _hitRegions.Add(new PetHitRegion(actionId, pillX, pillY, pillW, pillH, actionLabel));
    }

    private static void DrawHiraishinAfterimage(PixelBuffer buf, int cx, int bottomY, int alpha)
    {
        // Translucent golden Minato Hiraishin flash silhouette (spiky hair + Hokage cloak + boots)
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

    private void RenderMixiChillMode(PixelBuffer buf, float timeSeconds)
    {
        const int mixiCx = 330;
        const int mixiGroundY = GroundY - 2; // 122 -> spans y = 76..122, x = 290..368 (zero clipping!)

        MixiChillActivity activity = MixiChillRenderer.DrawChillCorner(buf, mixiCx, mixiGroundY, timeSeconds);
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

        // Clickable hit region covering the Do Mixi Gaming chill corner & badge
        int hitLeft = Math.Min(pillX, mixiCx - 40);
        int hitRight = Math.Min(CanvasWidth, Math.Max(pillX + pillW, mixiCx + 38));
        _hitRegions.Add(new PetHitRegion("agent:mixi-chill", hitLeft, pillY, hitRight - hitLeft, mixiGroundY - pillY + 2, badgeText));
    }

    private void RenderIdleChillMode(PixelBuffer buf, HqState state, float timeSeconds, bool emitTick)
    {
        const int primaryX = 346;

        string primarySprite = _idlePrimaryNonProcess != null
            ? ShinobiSpriteRenderer.NormalizeSpriteKey(_idlePrimaryNonProcess.SpriteKey)
            : "kakashi";
        if (primarySprite is not ("kakashi" or "shikamaru"))
        {
            primarySprite = "kakashi";
        }
        string primaryName = _idlePrimaryNonProcess?.Name ?? primarySprite;
        string primaryActionId = _cachedIdlePrimaryActionId ?? $"agent:{primarySprite}";
        int primaryVariant = _idlePrimaryNonProcess?.BehaviorVariant ?? 0;

        // Draw primary idle agent
        ShinobiSpriteRenderer.DrawCharacter(buf, primarySprite, AgentState.Idle, primaryX, GroundY, timeSeconds, behaviorVariant: primaryVariant);
        _hitRegions.Add(new PetHitRegion(primaryActionId, primaryX - 16, GroundY - 30, 32, 32, primaryName));

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

            ShinobiSpriteRenderer.DrawCharacter(buf, tSprite, AgentState.Idle, tx, GroundY, timeSeconds, phaseOffset: i + 1, behaviorVariant: tAg.BehaviorVariant);
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
            PixelFont.DrawTextCentered(buf, bX + bW / 2, bY + 5, primarySprite == "kakashi" ? "READING.." : "COFFEE..", ColorUtil.FromRgb(203, 213, 225), PixelFontSize.Small);
            PixelFont.DrawTextCentered(buf, bX + bW / 2, bY + 13, "HQ IDLE", ColorSlate, PixelFontSize.Small);
        }
    }

    private static string ShortenName(string name, string? id = null)
    {
        string clean = AgentAppearanceRegistry.SanitizeDisplayName(name, id);
        int idx = clean.IndexOf('(');
        if (idx > 0) clean = clean[..idx];
        return clean.Trim();
    }
}
