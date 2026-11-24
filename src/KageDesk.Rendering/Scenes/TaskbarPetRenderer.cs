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

    private readonly List<PetHitRegion> _hitRegions = new(8);
    private readonly ParticleSystem _particles = new();
    private readonly AgentSnapshot[] _workingScratch = new AgentSnapshot[3];
    private readonly char[] _textScratch = new char[48];

    private ThemePack? _cachedTheme;
    private ResolvedScenePalette _palette;
    private float _lastTime = -1f;
    private float _emitAccum;

    public IReadOnlyList<PetHitRegion> HitRegions => _hitRegions;

    public void ResetEffects()
    {
        _particles.Reset();
        _lastTime = -1f;
        _emitAccum = 0f;
    }

    public IReadOnlyList<PetHitRegion> Render(
        PixelBuffer buf,
        HqState state,
        ThemePack theme,
        float timeSeconds,
        bool showIdleChill = true)
    {
        if (buf.Width < CanvasWidth || buf.Height < CanvasHeight)
        {
            throw new ArgumentException($"PixelBuffer must be at least {CanvasWidth}x{CanvasHeight}.", nameof(buf));
        }

        if (!ReferenceEquals(_cachedTheme, theme))
        {
            _cachedTheme = theme;
            _palette = new ResolvedScenePalette(theme);
        }

        float dt = _lastTime < 0f ? 0.033f : Math.Clamp(timeSeconds - _lastTime, 0f, 0.2f);
        _lastTime = timeSeconds;

        // 100% transparent background (0x00000000)
        buf.ClearTransparent();
        _hitRegions.Clear();

        // Priority scan without LINQ allocations
        AgentSnapshot? approvalAgent = null;
        AgentSnapshot? failedAgent = null;
        AgentSnapshot? doneAgent = null;
        int workingCount = 0;

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
            else if (ag.State == AgentState.Working && workingCount < 3)
            {
                _workingScratch[workingCount++] = ag;
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
            RenderWorkingMode(buf, workingCount, timeSeconds, emitTick);
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

        // Waving character standing flush on taskbar edge (y = 124)
        ShinobiSpriteRenderer.DrawCharacter(buf, sprite, AgentState.NeedsApproval, charX, GroundY, timeSeconds);
        int pulseFill = ((int)(timeSeconds * 5f) & 1) == 0 ? ColorAmber : ColorAmberBright;
        ShinobiSpriteRenderer.DrawStatusBubble(buf, charX, GroundY - 29, "!", pulseFill, ColorOutline, ColorOutline);
        _hitRegions.Add(new PetHitRegion($"agent:{agent.Id}", charX - 16, GroundY - 40, 32, 42, agent.Name));

        // Crisp dark-amber pixel speech card to the left of waving agent
        const int cardX = 82;
        const int cardY = 48;
        const int cardW = 230;
        const int cardH = 74;

        // Outer shadow & crisp double amber frame
        buf.BlendRect(cardX + 2, cardY + 2, cardW, cardH, ColorOutline, 150);
        int borderCol = ((int)(timeSeconds * 4f) & 1) == 0 ? ColorAmber : ColorAmberBright;
        buf.DrawPixelCard(cardX, cardY, cardW, cardH, ColorAmberCardBg, borderCol, ColorUtil.WithAlpha(ColorAmberBright, 80));

        // Speech tail pointing right toward waving agent
        buf.FillRect(cardX + cardW - 1, cardY + 46, 5, 7, borderCol);
        buf.FillRect(cardX + cardW - 1, cardY + 47, 4, 5, ColorAmberCardBg);
        buf.FillRect(cardX + cardW + 3, cardY + 48, 3, 3, borderCol);
        buf.SetPixel(cardX + cardW + 3, cardY + 49, ColorAmberCardBg);

        // Header bar
        buf.FillRect(cardX + 2, cardY + 2, cardW - 4, 14, ColorAmberHeaderBg);
        buf.FillRect(cardX + 2, cardY + 16, cardW - 4, 1, ColorUtil.Darken(ColorAmber, 0.35f));

        // Pulsing status dot + Agent Name + NEEDS APPROVAL badge
        buf.FillRect(cardX + 6, cardY + 7, 4, 4, pulseFill);
        string shortName = ShortenName(agent.Name);
        PixelFont.DrawTextTruncated(buf, cardX + 14, cardY + 6, 122, shortName, ColorWhite, PixelFontSize.Regular);
        PixelFont.DrawText(buf, cardX + cardW - 60, cardY + 7, "APPROVAL REQ", ColorAmberBright, PixelFontSize.Small);

        // Question / Prompt text (up to 2 crisp wrapped lines)
        string prompt = !string.IsNullOrWhiteSpace(agent.QuestionPrompt)
            ? agent.QuestionPrompt!
            : (!string.IsNullOrWhiteSpace(agent.TaskTitle) ? agent.TaskTitle! : "WAITING FOR YOUR APPROVAL");

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

        // Action Buttons row: [OPEN ORCA] and [X]
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
        ShinobiSpriteRenderer.DrawCharacter(buf, sprite, AgentState.Failed, charX, GroundY, timeSeconds);
        _hitRegions.Add(new PetHitRegion($"agent:{agent.Id}", charX - 16, GroundY - 30, 32, 32, agent.Name));

        if (emitTick)
        {
            _particles.EmitFireAndSmoke(laptopX, GroundY - 9);
        }

        // Crisp red pixel alert bubble
        const int cardX = 114;
        const int cardY = 66;
        const int cardW = 184;
        const int cardH = 54;
        int border = ((int)(timeSeconds * 6f) & 1) == 0 ? ColorCrimson : ColorUtil.FromRgb(252, 165, 165);

        buf.DrawPixelCard(cardX, cardY, cardW, cardH, ColorCrimsonCardBg, border);
        buf.FillRect(cardX + cardW - 1, cardY + 34, 4, 5, border);
        buf.FillRect(cardX + cardW - 1, cardY + 35, 3, 3, ColorCrimsonCardBg);

        string shortName = ShortenName(agent.Name);
        int written = 0;
        "FAIL \u2022 ".AsSpan().CopyTo(_textScratch.AsSpan());
        written += 7;
        ReadOnlySpan<char> snSpan = shortName.AsSpan();
        int toCopy = Math.Min(snSpan.Length, _textScratch.Length - written);
        snSpan.Slice(0, toCopy).CopyTo(_textScratch.AsSpan(written));
        written += toCopy;
        PixelFont.DrawTextTruncated(buf, cardX + 8, cardY + 7, cardW - 16, _textScratch.AsSpan(0, written), ColorCrimson, PixelFontSize.Regular);
        PixelFont.DrawTextTruncated(buf, cardX + 8, cardY + 20, cardW - 16, "FAIL \u2022 CLICK DETAILS", ColorUtil.FromRgb(254, 202, 202), PixelFontSize.Small);

        // Buttons
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

        ShinobiSpriteRenderer.DrawCharacter(buf, sprite, AgentState.Done, charX, GroundY, timeSeconds);
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
        PixelFont.DrawTextTruncated(buf, cardX + 42, cardY + 10, cardW - 50, ShortenName(agent.Name), ColorWhite, PixelFontSize.Small);
        string msg = !string.IsNullOrWhiteSpace(agent.TaskTitle) ? agent.TaskTitle! : "TASK COMPLETE!";
        PixelFont.DrawTextTruncated(buf, cardX + 8, cardY + 24, cardW - 16, msg, ColorUtil.FromRgb(167, 243, 208), PixelFontSize.Small);

        _hitRegions.Add(new PetHitRegion("open_orca", cardX, cardY, cardW, cardH, "Open Orca"));
    }

    private void RenderWorkingMode(PixelBuffer buf, int workingCount, float timeSeconds, bool emitTick)
    {
        // Up to 3 active working agents side-by-side at mini pixel desks on right edge
        const int spacing = 46;
        const int rightmostX = 346;

        for (int i = 0; i < workingCount; i++)
        {
            AgentSnapshot ag = _workingScratch[i];
            int cx = rightmostX - i * spacing;
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);

            // Character behind mini desk with clear head & shoulders visible
            ShinobiSpriteRenderer.DrawCharacter(buf, sprite, AgentState.Working, cx, GroundY - 6, timeSeconds, phaseOffset: i, behindDesk: true);
            ShinobiSpriteRenderer.DrawMiniTaskbarDesk(buf, cx, GroundY, _palette.DeskTop, AgentState.Working, timeSeconds);

            _hitRegions.Add(new PetHitRegion($"agent:{ag.Id}", cx - 18, GroundY - 36, 36, 38, ag.Name));

            if (emitTick)
            {
                if (sprite == "minato") _particles.EmitSparks(cx, GroundY - 20, 2);
                else if (sprite == "tsunade") _particles.EmitChakraBubble(cx + 8, GroundY - 13);
            }
        }

        // Crisp cyan status pill tightly sized to text, centered over active working agents!
        ReadOnlySpan<char> pillText;
        if (workingCount == 1)
        {
            AgentSnapshot a = _workingScratch[0];
            string shortName = ShortenName(a.Name);
            string tool = NormalizeShortTool(a.ToolName);
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
            ReadOnlySpan<char> t = tool.AsSpan();
            int c2 = Math.Min(t.Length, _textScratch.Length - written);
            t.Slice(0, c2).CopyTo(_textScratch.AsSpan(written));
            written += c2;
            pillText = _textScratch.AsSpan(0, written);
        }
        else
        {
            _textScratch[0] = (char)('0' + workingCount);
            " AGENTS WORKING".AsSpan().CopyTo(_textScratch.AsSpan(1));
            pillText = _textScratch.AsSpan(0, 16);
        }

        int textW = PixelFont.MeasureWidth(pillText, PixelFontSize.Small);
        int pillW = Math.Min(164, Math.Max(78, textW + 24));
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

        _hitRegions.Add(new PetHitRegion("open_orca", pillX, pillY, pillW, pillH, "Open Orca"));
    }

    private void RenderIdleChillMode(PixelBuffer buf, HqState state, float timeSeconds, bool emitTick)
    {
        const int charX = 346;

        // Pick Kakashi reading orange book or Shikamaru sipping coffee
        AgentSnapshot? idleAgent = state.Agents.Count > 0 ? state.Agents[0] : null;
        string sprite = idleAgent != null
            ? ShinobiSpriteRenderer.NormalizeSpriteKey(idleAgent.SpriteKey)
            : "kakashi";
        if (sprite is not ("kakashi" or "shikamaru"))
        {
            sprite = "kakashi";
        }
        string agentId = idleAgent?.Id ?? sprite;

        ShinobiSpriteRenderer.DrawCharacter(buf, sprite, AgentState.Idle, charX, GroundY, timeSeconds);

        if (sprite == "shikamaru")
        {
            ShinobiSpriteRenderer.DrawCoffeeCup(buf, charX - 15, GroundY - 5, timeSeconds);
            if (emitTick) _particles.EmitCoffeeSteam(charX - 13, GroundY - 6);
        }

        // Subtle chill bubble
        const int bX = 248;
        const int bY = 92;
        const int bW = 76;
        const int bH = 22;
        buf.DrawPixelCard(bX, bY, bW, bH, ColorUtil.FromRgb(22, 26, 38), ColorSlate);
        PixelFont.DrawTextCentered(buf, bX + bW / 2, bY + 5, sprite == "kakashi" ? "READING.." : "COFFEE..", ColorUtil.FromRgb(203, 213, 225), PixelFontSize.Small);
        PixelFont.DrawTextCentered(buf, bX + bW / 2, bY + 13, "HQ IDLE", ColorSlate, PixelFontSize.Small);

        _hitRegions.Add(new PetHitRegion($"agent:{agentId}", charX - 16, GroundY - 30, 32, 32, sprite));
    }

    private static string ShortenName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "AGENT";
        int idx = name.IndexOf('(');
        if (idx > 0) name = name[..idx];
        return name.Trim();
    }

    private static string NormalizeShortTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return "WORK";
        if (toolName.Contains("edit", StringComparison.OrdinalIgnoreCase) || toolName.Contains("write", StringComparison.OrdinalIgnoreCase)) return "EDIT";
        if (toolName.Contains("test", StringComparison.OrdinalIgnoreCase)) return "TEST";
        if (toolName.Contains("review", StringComparison.OrdinalIgnoreCase)) return "REVIEW";
        if (toolName.Contains("exec", StringComparison.OrdinalIgnoreCase) || toolName.Contains("compile", StringComparison.OrdinalIgnoreCase)) return "EXEC";
        return toolName.Length > 8 ? toolName[..8].ToUpperInvariant() : toolName.ToUpperInvariant();
    }
}
