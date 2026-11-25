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

    private readonly List<PetHitRegion> _hitRegions = new(16);
    private readonly ParticleSystem _particles = new();
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

    private ThemePack? _cachedTheme;
    private ResolvedScenePalette _palette;
    private float _lastTime = -1f;
    private float _emitAccum;
    private DateTimeOffset _stateTimestamp;

    public IReadOnlyList<PetHitRegion> HitRegions => _hitRegions;

    public void ResetEffects()
    {
        _particles.Reset();
        _lastTime = -1f;
        _emitAccum = 0f;
        _lastRenderedState = null;
    }

    public IReadOnlyList<PetHitRegion> Render(
        PixelBuffer buf,
        HqState state,
        ThemePack theme,
        float timeSeconds,
        bool showIdleChill = true,
        DateTimeOffset? nowOverride = null)
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

        if (!ReferenceEquals(state, _lastRenderedState))
        {
            _lastRenderedState = state;
            for (int i = 0; i < workingCount; i++)
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
            if (workingCount == 1)
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
                    int extra = totalWorking - workingCount;
                    _cachedWorkingMultiLabel = $"{workingCount} WORKING (+{extra})";
                }
                else
                {
                    _cachedWorkingMultiLabel = $"{totalWorking} AGENTS WORKING";
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

        _emitAccum += dt;
        bool emitTick = false;
        if (_emitAccum >= 0.14f)
        {
            _emitAccum = 0f;
            emitTick = true;
        }

        if (approvalAgent != null)
        {
            RenderApprovalMode(buf, approvalAgent, timeSeconds);
        }
        else if (failedAgent != null)
        {
            RenderFailedMode(buf, failedAgent, timeSeconds, emitTick);
        }
        else if (doneAgent != null)
        {
            RenderDoneMode(buf, doneAgent, timeSeconds, emitTick);
        }
        else if (workingCount > 0)
        {
            RenderWorkingMode(buf, workingCount, totalWorking, timeSeconds, emitTick);
        }
        else if (showIdleChill)
        {
            RenderIdleChillMode(buf, state, timeSeconds, emitTick);
        }

        _particles.Update(dt);
        _particles.Render(buf);

        return _hitRegions;
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

    private void RenderWorkingMode(PixelBuffer buf, int workingCount, int totalWorking, float timeSeconds, bool emitTick)
    {
        int spacing = workingCount <= 4 ? 46 : 38;
        const int rightmostX = 346;

        for (int i = 0; i < workingCount; i++)
        {
            AgentSnapshot ag = _workingScratch[i];
            int cx = rightmostX - i * spacing;
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);
            AgentActionKind actionKind = _cachedWorkingItems[i].ActionKind;

            // Character behind mini desk with clear head & shoulders visible
            ShinobiSpriteRenderer.DrawCharacter(
                buf,
                sprite,
                AgentState.Working,
                cx,
                GroundY - 6,
                timeSeconds,
                phaseOffset: i,
                behindDesk: true,
                actionKind: actionKind,
                behaviorVariant: ag.BehaviorVariant);

            ShinobiSpriteRenderer.DrawMiniTaskbarDesk(
                buf,
                cx,
                GroundY,
                _palette.DeskTop,
                AgentState.Working,
                timeSeconds,
                actionKind: actionKind,
                deskRig: ag.DeskRig,
                behaviorVariant: ag.BehaviorVariant);

            _hitRegions.Add(new PetHitRegion(_cachedWorkingItems[i].HitRegionAction, cx - 18, GroundY - 36, 36, 38, ag.Name));

            if (emitTick)
            {
                if (sprite == "minato") _particles.EmitSparks(cx, GroundY - 20, 2);
                else if (sprite == "tsunade") _particles.EmitChakraBubble(cx + 8, GroundY - 13);
            }
        }

        // Status pill text
        ReadOnlySpan<char> pillText;
        if (workingCount == 1)
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
                ? $"{workingCount} WORKING (+{totalWorking - workingCount})"
                : $"{totalWorking} AGENTS WORKING");
            pillText = label.AsSpan();
        }

        int textW = PixelFont.MeasureWidth(pillText, PixelFontSize.Small);
        int pillW = Math.Min(172, Math.Max(78, textW + 24));
        int pillH = 18;
        int groupCenterX = rightmostX - ((workingCount - 1) * spacing) / 2;
        int pillX = Math.Clamp(groupCenterX - pillW / 2, 8, CanvasWidth - pillW - 6);
        int pillY = GroundY - 58;

        buf.BlendRect(pillX + 1, pillY + 1, pillW, pillH, ColorOutline, 130);
        buf.DrawPixelCard(pillX, pillY, pillW, pillH, ColorCyanCardBg, ColorCyan, ColorUtil.WithAlpha(ColorWhite, 40));

        // Pulsing cyan LED
        int ledAlpha = 170 + (int)(MathF.Sin(timeSeconds * 6f) * 75f);
        buf.BlendRect(pillX + 7, pillY + 7, 4, 4, ColorCyan, ledAlpha);
        PixelFont.DrawTextTruncated(buf, pillX + 15, pillY + 6, pillW - 21, pillText, ColorWhite, PixelFontSize.Small);

        string actionId = (workingCount == 1 && string.Equals(_workingScratch[0].Source, "process", StringComparison.OrdinalIgnoreCase))
            ? (_cachedSingleWorkingFocusActionId ?? $"focus_agent:{_workingScratch[0].Id}")
            : "open_orca";
        string actionLabel = (workingCount == 1 && string.Equals(_workingScratch[0].Source, "process", StringComparison.OrdinalIgnoreCase))
            ? _workingScratch[0].Name
            : "Open Orca";

        _hitRegions.Add(new PetHitRegion(actionId, pillX, pillY, pillW, pillH, actionLabel));
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
