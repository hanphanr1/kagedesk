using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Sprites;

namespace KageDesk.Rendering.Scenes;

public sealed class HqWallpaperRenderer
{
    public const int CanvasWidth = 384;
    public const int CanvasHeight = 216;

    private static readonly int ColorOutline = ColorUtil.FromRgb(14, 16, 24);
    private static readonly int ColorEmerald = ColorUtil.FromRgb(34, 197, 94);
    private static readonly int ColorEmeraldDark = ColorUtil.FromRgb(16, 56, 36);
    private static readonly int ColorAmber = ColorUtil.FromRgb(245, 158, 11);
    private static readonly int ColorAmberBright = ColorUtil.FromRgb(253, 224, 71);
    private static readonly int ColorAmberDark = ColorUtil.FromRgb(68, 42, 12);
    private static readonly int ColorCrimson = ColorUtil.FromRgb(239, 68, 68);
    private static readonly int ColorCrimsonDark = ColorUtil.FromRgb(74, 18, 26);
    private static readonly int ColorCyan = ColorUtil.FromRgb(34, 211, 238);
    private static readonly int ColorCyanDark = ColorUtil.FromRgb(14, 52, 74);
    private static readonly int ColorSlate = ColorUtil.FromRgb(148, 163, 184);
    private static readonly int ColorSlateDark = ColorUtil.FromRgb(30, 41, 59);
    private static readonly int ColorWhite = ColorUtil.FromRgb(248, 250, 252);

    private static readonly int[] CoreStationCentersX = { 56, 146, 238, 328 };
    private static readonly string[] CoreDefaultNames = { "SHIKAMARU", "MINATO", "TSUNADE", "KAKASHI" };
    private static readonly string[] CoreDefaultSprites = { "shikamaru", "minato", "tsunade", "kakashi" };

    internal struct StationSlot
    {
        public bool Occupied;
        public string Name;
        public string SpriteKey;
        public string StationType;
        public AgentState State;
        public string? ToolName;
        public int VariantSeed;
        public AgentActionKind ActionKind;
        public string BadgeLabel;
        public NameplateLayout Layout;
        public int BorderCol;
        public int BadgeBg;
        public int BadgeFg;
    }

    private readonly StationSlot[] _coreSlots = new StationSlot[4];
    private readonly StationSlot[] _extraSlots = new StationSlot[5];
    private readonly List<AgentSnapshot> _extraCandidates = new(16);
    private static readonly Comparison<AgentSnapshot> ExtraStationComparison = (a, b) =>
        ComputeExtraStationPriority(b).CompareTo(ComputeExtraStationPriority(a));
    private readonly ParticleSystem _particles = new();
    private readonly char[] _fmtScratch = new char[48];

    private ThemePack? _cachedTheme;
    private ResolvedScenePalette _palette;
    private readonly int[] _cachedBackdropNoExtra = new int[CanvasWidth * CanvasHeight];
    private readonly int[] _cachedBackdropWithExtra = new int[CanvasWidth * CanvasHeight];
    private ThemePack? _backdropTheme;
    private bool _backdropNoExtraValid;
    private bool _backdropWithExtraValid;

    private float _lastTime = -1f;
    private float _emitAccum;
    private int _lastCloneCount;
    private string? _cachedPrimaryBranch;
    private string? _cachedPrimaryWorkflow;
    private string? _cachedBranchText;
    private HqState? _lastAssignedState;
    private int _cachedExtraCount;
    private AccountInfo? _cachedActiveAccount;
    private string? _cachedFirstAgentRepo;

    public void ResetEffects()
    {
        _particles.Reset();
        _lastTime = -1f;
        _emitAccum = 0f;
        _lastCloneCount = 0;
        _lastAssignedState = null;
    }

    public void Render(PixelBuffer buf, HqState state, ThemePack theme, float timeSeconds)
    {
        if (buf.Width < CanvasWidth || buf.Height < CanvasHeight)
        {
            throw new ArgumentException($"PixelBuffer must be at least {CanvasWidth}x{CanvasHeight}.", nameof(buf));
        }

        if (!ReferenceEquals(_cachedTheme, theme))
        {
            _cachedTheme = theme;
            _palette = new ResolvedScenePalette(theme);
            _backdropNoExtraValid = false;
            _backdropWithExtraValid = false;
            _backdropTheme = theme;
            _lastAssignedState = null;
        }

        float dt = _lastTime < 0f ? 0.033f : Math.Clamp(timeSeconds - _lastTime, 0f, 0.2f);
        _lastTime = timeSeconds;

        int extraCount;
        if (!ReferenceEquals(state, _lastAssignedState))
        {
            _lastAssignedState = state;
            _cachedExtraCount = AssignStations(state);
        }
        extraCount = _cachedExtraCount;

        bool hasExtraRow = extraCount > 0;
        bool anyApproval = false;
        int approvalAlertX = -1;
        int approvalAlertY = -1;

        int coreDeskY = hasExtraRow ? 104 : 126;
        int extraDeskY = 168;

        for (int i = 0; i < 4; i++)
        {
            if (_coreSlots[i].State == AgentState.NeedsApproval)
            {
                anyApproval = true;
                approvalAlertX = CoreStationCentersX[i];
                approvalAlertY = coreDeskY;
            }
        }
        for (int i = 0; i < extraCount; i++)
        {
            if (_extraSlots[i].State == AgentState.NeedsApproval)
            {
                anyApproval = true;
                approvalAlertX = GetExtraStationCenterX(i, extraCount);
                approvalAlertY = extraDeskY;
            }
        }

        // 1. Walls & Floor & Rug
        DrawRoomBackdrop(buf, timeSeconds, hasExtraRow);

        // 2. Top Wall Boards & Weather Window
        DrawAccountsBoard(buf, state, timeSeconds);
        DrawWeatherWindow(buf, state.Weather, timeSeconds);
        DrawBuildCiBoard(buf, state, timeSeconds);

        // 3. Theme-Specific Room Decor Props (scroll/bonsai, server racks, desk lamp/cat)
        DrawThemeProps(buf, timeSeconds, hasExtraRow);

        // 4. Emit & Update Particles (zero-alloc)
        UpdateStationParticles(dt, extraCount, coreDeskY, extraDeskY, state);

        // 5. Row 1: 4 Core Character Workstations
        for (int i = 0; i < 4; i++)
        {
            DrawWorkstation(buf, ref _coreSlots[i], CoreStationCentersX[i], coreDeskY, timeSeconds, i);
        }

        // 6. Row 2: Dynamic Clone / External Workstations (up to 5)
        for (int i = 0; i < extraCount; i++)
        {
            int cx = GetExtraStationCenterX(i, extraCount);
            DrawWorkstation(buf, ref _extraSlots[i], cx, extraDeskY, timeSeconds, i + 4);
        }

        // 7. Render Active Particles on top
        _particles.Render(buf);

        // 8. Approval Spotlight & Pulsing Amber Border
        if (anyApproval)
        {
            DrawApprovalSpotlight(buf, approvalAlertX, approvalAlertY, timeSeconds);
        }
    }

    private int AssignStations(HqState state)
    {
        for (int i = 0; i < 4; i++)
        {
            _coreSlots[i] = new StationSlot
            {
                Occupied = false,
                Name = CoreDefaultNames[i],
                SpriteKey = CoreDefaultSprites[i],
                StationType = CoreDefaultSprites[i],
                State = AgentState.Idle,
                ToolName = null,
                VariantSeed = i,
                ActionKind = AgentActionKind.None,
                BadgeLabel = "IDLE"
            };
        }
        for (int i = 0; i < 5; i++)
        {
            _extraSlots[i] = default;
        }

        _extraCandidates.Clear();
        var agents = state.Agents;

        for (int i = 0; i < agents.Count; i++)
        {
            AgentSnapshot ag = agents[i];
            int coreIndex = ResolveCoreSlotIndex(ag);

            if (coreIndex >= 0 && !_coreSlots[coreIndex].Occupied)
            {
                var (actionKind, badgeLabel) = AgentActionClassifier.Classify(ag.State, ag.ToolName, ag.TaskTitle);
                _coreSlots[coreIndex] = new StationSlot
                {
                    Occupied = true,
                    Name = ShortenDisplayName(ag.Name),
                    SpriteKey = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey),
                    StationType = CoreDefaultSprites[coreIndex],
                    State = ag.State,
                    ToolName = ag.ToolName,
                    VariantSeed = HashString(ag.Id),
                    ActionKind = actionKind,
                    BadgeLabel = badgeLabel
                };
            }
            else
            {
                _extraCandidates.Add(ag);
            }
        }

        // Priority sort for extra candidates (Row 2):
        _extraCandidates.Sort(ExtraStationComparison);

        int extraCount = Math.Min(5, _extraCandidates.Count);
        for (int i = 0; i < extraCount; i++)
        {
            AgentSnapshot ag = _extraCandidates[i];
            string sprite = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);
            if (ag.Role == AgentRole.Clone) sprite = "clone";

            var (actionKind, badgeLabel) = AgentActionClassifier.Classify(ag.State, ag.ToolName, ag.TaskTitle);
            _extraSlots[i] = new StationSlot
            {
                Occupied = true,
                Name = ShortenDisplayName(ag.Name),
                SpriteKey = sprite,
                StationType = "clone",
                State = ag.State,
                ToolName = ag.ToolName,
                VariantSeed = HashString(ag.Id),
                ActionKind = actionKind,
                BadgeLabel = badgeLabel
            };
        }

        bool hasExtraRow = extraCount > 0;
        int coreDeskY = hasExtraRow ? 104 : 126;
        int extraDeskY = 168;

        int coreY = coreDeskY + 20;
        for (int i = 0; i < 4; i++)
        {
            FinalizeSlotLayout(ref _coreSlots[i], CoreStationCentersX[i], coreY, 84);
        }

        int extraY = extraDeskY + 20;
        int maxExtraPlateWidth = extraCount == 5 ? 68 : 84;
        for (int i = 0; i < extraCount; i++)
        {
            int cx = GetExtraStationCenterX(i, extraCount);
            FinalizeSlotLayout(ref _extraSlots[i], cx, extraY, maxExtraPlateWidth);
        }

        _cachedActiveAccount = null;
        for (int i = 0; i < state.Accounts.Count; i++)
        {
            if (state.Accounts[i].IsActive) { _cachedActiveAccount = state.Accounts[i]; break; }
        }
        if (_cachedActiveAccount == null && state.Accounts.Count > 0) _cachedActiveAccount = state.Accounts[0];

        _cachedFirstAgentRepo = null;
        for (int i = 0; i < state.Agents.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(state.Agents[i].Repo)) { _cachedFirstAgentRepo = state.Agents[i].Repo; break; }
        }

        return extraCount;
    }

    private void FinalizeSlotLayout(ref StationSlot slot, int cx, int y, int maxPlateWidth)
    {
        ReadOnlySpan<char> badge = (slot.BadgeLabel ?? "IDLE").AsSpan();
        ReadOnlySpan<char> nameSpan = slot.Name.AsSpan();
        if (nameSpan.Length > 10) nameSpan = nameSpan.Slice(0, 10);
        slot.Layout = ComputeNameplateLayout(cx, y, nameSpan, badge, maxPlateWidth);

        (slot.BadgeBg, slot.BadgeFg) = slot.State switch
        {
            AgentState.Working => (ColorCyanDark, ColorCyan),
            AgentState.NeedsApproval => (ColorAmberDark, ColorAmberBright),
            AgentState.Done => (ColorEmeraldDark, ColorEmerald),
            AgentState.Failed => (ColorCrimsonDark, ColorCrimson),
            _ => (ColorSlateDark, ColorSlate)
        };
        slot.BorderCol = slot.State switch
        {
            AgentState.NeedsApproval => ColorAmber,
            AgentState.Failed => ColorCrimson,
            AgentState.Done => ColorEmerald,
            AgentState.Working => ColorCyan,
            _ => _palette.Border
        };
    }

    private static int ComputeExtraStationPriority(AgentSnapshot ag)
    {
        if (ag.State == AgentState.NeedsApproval) return 1000;
        if (ag.State == AgentState.Failed) return 900;
        if (ag.State == AgentState.Working) return 800;
        if (ag.State == AgentState.Done) return 700;
        if (string.Equals(ag.Source, "process", StringComparison.OrdinalIgnoreCase)) return 500;
        if (string.Equals(ag.Source, "push", StringComparison.OrdinalIgnoreCase)) return 400;
        if (ag.Unread || !string.IsNullOrWhiteSpace(ag.TaskTitle)) return 200;
        return 50;
    }

    private static int ResolveCoreSlotIndex(AgentSnapshot ag)
    {
        if (ag.Role == AgentRole.Clone || ag.Role == AgentRole.External) return -1;
        if (string.Equals(ag.Source, "process", StringComparison.OrdinalIgnoreCase)) return -1;

        string key = ShinobiSpriteRenderer.NormalizeSpriteKey(ag.SpriteKey);
        if (key == "shikamaru" || ag.Role == AgentRole.Leader) return 0;
        if (key == "minato" || ag.Role == AgentRole.Coder) return 1;
        if (key == "tsunade" || ag.Role == AgentRole.Tester) return 2;
        if (key == "kakashi" || ag.Role == AgentRole.Reviewer) return 3;
        return -1;
    }

    private static int GetExtraStationCenterX(int index, int totalCount)
    {
        return totalCount switch
        {
            1 => 192,
            2 => index == 0 ? 132 : 252,
            3 => index switch { 0 => 96, 1 => 192, _ => 288 },
            4 => CoreStationCentersX[index & 3],
            5 => index switch { 0 => 48, 1 => 120, 2 => 192, 3 => 264, _ => 336 },
            _ => CoreStationCentersX[index % 4]
        };
    }

    private void DrawRoomBackdrop(PixelBuffer buf, float timeSeconds, bool hasExtraRow)
    {
        if (hasExtraRow)
        {
            if (_backdropWithExtraValid && ReferenceEquals(_backdropTheme, _cachedTheme))
            {
                _cachedBackdropWithExtra.AsSpan().CopyTo(buf.Pixels);
            }
            else
            {
                RenderStaticBackdropTo(buf, hasExtraRow: true);
                buf.ReadOnlyPixels.CopyTo(_cachedBackdropWithExtra);
                _backdropWithExtraValid = true;
                _backdropTheme = _cachedTheme;
            }
        }
        else
        {
            if (_backdropNoExtraValid && ReferenceEquals(_backdropTheme, _cachedTheme))
            {
                _cachedBackdropNoExtra.AsSpan().CopyTo(buf.Pixels);
            }
            else
            {
                RenderStaticBackdropTo(buf, hasExtraRow: false);
                buf.ReadOnlyPixels.CopyTo(_cachedBackdropNoExtra);
                _backdropNoExtraValid = true;
                _backdropTheme = _cachedTheme;
            }
        }

        if (_palette.SkylineStyle == "cyber")
        {
            int rugX = 24;
            int rugY = hasExtraRow ? 98 : 112;
            int rugW = 336;
            int rugH = hasExtraRow ? 104 : 88;
            int pulse = 140 + (int)(MathF.Sin(timeSeconds * 3.5f) * 55f);
            buf.DrawOutlineRect(rugX + 2, rugY + 2, rugW - 4, rugH - 4, ColorUtil.WithAlpha(ColorCyan, pulse));
        }
    }

    private void RenderStaticBackdropTo(PixelBuffer buf, bool hasExtraRow)
    {
        // Upper wall (y: 0..71)
        buf.FillVerticalGradient(0, 0, CanvasWidth, 72, _palette.WallTop, _palette.WallBottom);

        // Subtle vertical wall paneling
        int panelLine = ColorUtil.Darken(_palette.WallBottom, 0.25f);
        for (int wx = 32; wx < CanvasWidth; wx += 32)
        {
            buf.FillRect(wx, 0, 1, 69, panelLine);
        }

        // Wainscoting / Baseboard molding at y = 68..72
        buf.FillRect(0, 68, CanvasWidth, 4, ColorUtil.Darken(_palette.WindowFrame, 0.2f));
        buf.FillRect(0, 68, CanvasWidth, 1, ColorUtil.Lighten(_palette.WindowFrame, 0.12f));
        buf.FillRect(0, 71, CanvasWidth, 1, ColorOutline);

        // Floor (y: 72..216)
        buf.FillRect(0, 72, CanvasWidth, CanvasHeight - 72, _palette.FloorBase);

        if (_palette.SkylineStyle == "cyber")
        {
            // Neon cyan tactical floor grid
            for (int fy = 80; fy < CanvasHeight; fy += 16)
            {
                buf.FillRect(0, fy, CanvasWidth, 1, _palette.FloorLine);
            }
            for (int fx = 24; fx < CanvasWidth; fx += 32)
            {
                buf.FillRect(fx, 72, 1, CanvasHeight - 72, _palette.FloorLine);
                for (int fy = 80; fy < CanvasHeight; fy += 16)
                {
                    buf.BlendPixel(fx, fy, ColorCyan, 120);
                }
            }
        }
        else
        {
            // Rich wood planks with staggered vertical seams
            int plankHi = ColorUtil.Lighten(_palette.FloorBase, 0.06f);
            for (int fy = 82; fy < CanvasHeight; fy += 12)
            {
                buf.FillRect(0, fy, CanvasWidth, 1, _palette.FloorLine);
                buf.FillRect(0, fy + 1, CanvasWidth, 1, plankHi);

                int offset = ((fy / 12) & 1) == 0 ? 18 : 50;
                for (int fx = offset; fx < CanvasWidth; fx += 64)
                {
                    buf.FillRect(fx, fy - 11, 1, 11, _palette.FloorLine);
                }
            }
        }

        // Central Rug / Holo Mat framing the workstation floor symmetrically
        int rugX = 24;
        int rugY = hasExtraRow ? 98 : 112;
        int rugW = 336;
        int rugH = hasExtraRow ? 104 : 88;

        buf.FillRect(rugX - 1, rugY - 1, rugW + 2, rugH + 2, ColorOutline);
        buf.FillRect(rugX, rugY, rugW, rugH, _palette.RugOuter);
        buf.FillRect(rugX + 5, rugY + 5, rugW - 10, rugH - 10, _palette.RugInner);

        if (_palette.SkylineStyle == "cyber")
        {
            // Cyber holo mat neon corner brackets
            int magenta = ColorUtil.FromRgb(236, 72, 153);
            buf.FillRect(rugX + 1, rugY + 1, 10, 2, magenta);
            buf.FillRect(rugX + 1, rugY + 1, 2, 10, magenta);
            buf.FillRect(rugX + rugW - 11, rugY + 1, 10, 2, magenta);
            buf.FillRect(rugX + rugW - 3, rugY + 1, 2, 10, magenta);
            buf.FillRect(rugX + 1, rugY + rugH - 3, 10, 2, ColorCyan);
            buf.FillRect(rugX + rugW - 11, rugY + rugH - 3, 10, 2, ColorCyan);
        }
        else if (_palette.SkylineStyle == "lofi")
        {
            // Cozy terracotta woven border + cream pixel fringe tassels
            int trim = ColorUtil.FromRgb(251, 191, 36);
            int fringe = ColorUtil.FromRgb(245, 224, 188);
            buf.DrawOutlineRect(rugX + 3, rugY + 3, rugW - 6, rugH - 6, trim);
            for (int ty = rugY + 3; ty < rugY + rugH - 2; ty += 4)
            {
                buf.FillRect(rugX - 4, ty, 3, 2, fringe);
                buf.FillRect(rugX + rugW + 1, ty, 3, 2, fringe);
            }
        }
        else
        {
            // Shinobi HQ crimson-plum tatami weave & gold corner accents
            int gold = ColorUtil.FromRgb(217, 154, 62);
            buf.DrawOutlineRect(rugX + 3, rugY + 3, rugW - 6, rugH - 6, ColorUtil.Darken(_palette.RugOuter, 0.25f));
            buf.FillRect(rugX + 2, rugY + 2, 4, 4, gold);
            buf.FillRect(rugX + rugW - 6, rugY + 2, 4, 4, gold);
            buf.FillRect(rugX + 2, rugY + rugH - 6, 4, 4, gold);
            buf.FillRect(rugX + rugW - 6, rugY + rugH - 6, 4, 4, gold);
        }
    }

    private void DrawAccountsBoard(PixelBuffer buf, HqState state, float timeSeconds)
    {
        const int bx = 12;
        const int by = 12;
        const int bw = 100;
        const int bh = 54;

        buf.DrawPixelCard(bx, by, bw, bh, _palette.BoardBg, _palette.Border, ColorUtil.WithAlpha(ColorWhite, 35));

        // Header strip
        buf.FillRect(bx + 1, by + 1, bw - 2, 10, ColorUtil.Lighten(_palette.BoardBg, 0.08f));
        PixelFont.DrawText(buf, bx + 4, by + 4, "ACCOUNTS", _palette.Accent, PixelFontSize.Small);
        buf.FillRect(bx + 2, by + 11, bw - 4, 1, _palette.Border);

        AccountInfo? active = _cachedActiveAccount;
        var accounts = state.Accounts;

        if (active != null)
        {
            // Provider pill in top-right of header
            ReadOnlySpan<char> prov = active.Provider.AsSpan();
            if (prov.Length > 7) prov = prov.Slice(0, 7);
            int pw = PixelFont.MeasureWidth(prov, PixelFontSize.Small) + 4;
            int px = bx + bw - pw - 3;
            buf.FillRect(px, by + 3, pw, 7, ColorUtil.Darken(_palette.Accent, 0.55f));
            PixelFont.DrawText(buf, px + 2, by + 4, prov, ColorWhite, PixelFontSize.Small);

            // Active account label
            PixelFont.DrawTextTruncated(buf, bx + 4, by + 15, bw - 8, active.Label, _palette.Text, PixelFontSize.Small);

            // Quota bar OR live ACTIVE indicator
            if (active.QuotaPercent.HasValue)
            {
                int pct = Math.Clamp((int)Math.Round(active.QuotaPercent.Value), 0, 100);
                int barX = bx + 4;
                int barY = by + 25;
                int barW = 68;
                int barH = 8;
                int barColor = pct >= 50 ? ColorEmerald : (pct >= 20 ? ColorAmber : ColorCrimson);

                buf.FillRect(barX, barY, barW, barH, ColorOutline);
                buf.FillRect(barX + 1, barY + 1, barW - 2, barH - 2, ColorSlateDark);
                int fillW = ((barW - 2) * pct) / 100;
                if (fillW > 0)
                {
                    buf.FillRect(barX + 1, barY + 1, fillW, barH - 2, barColor);
                    buf.FillRect(barX + 1, barY + 1, fillW, 1, ColorUtil.Lighten(barColor, 0.25f));
                }

                // Format percentage without string alloc
                if (pct.TryFormat(_fmtScratch, out int written))
                {
                    _fmtScratch[written] = '%';
                    PixelFont.DrawText(buf, barX + barW + 3, barY + 1, _fmtScratch.AsSpan(0, written + 1), barColor, PixelFontSize.Small);
                }
            }
            else
            {
                int pulse = 180 + (int)(MathF.Sin(timeSeconds * 4f) * 60f);
                buf.FillRect(bx + 4, by + 25, bw - 8, 9, ColorEmeraldDark);
                buf.DrawOutlineRect(bx + 4, by + 25, bw - 8, 9, ColorEmerald);
                buf.BlendRect(bx + 7, by + 28, 3, 3, ColorEmerald, pulse);
                PixelFont.DrawText(buf, bx + 13, by + 27, "ACTIVE · LIVE", ColorEmerald, PixelFontSize.Small);
            }

            // Bottom managed account count
            int count = accounts.Count;
            if (count.TryFormat(_fmtScratch, out int wCount))
            {
                ReadOnlySpan<char> suffix = count == 1 ? " ACCOUNT" : " ACCOUNTS";
                suffix.CopyTo(_fmtScratch.AsSpan(wCount));
                PixelFont.DrawText(buf, bx + 4, by + 40, _fmtScratch.AsSpan(0, wCount + suffix.Length), ColorSlate, PixelFontSize.Small);
            }

            // Tiny colored dots representing managed accounts (max 4 dots)
            int dots = Math.Min(count, 4);
            for (int d = 0; d < dots; d++)
            {
                int dotColor = d == 0 ? ColorEmerald : (d == 1 ? ColorCyan : (d == 2 ? ColorAmber : ColorSlate));
                buf.FillRect(bx + bw - 6 - d * 5, by + 41, 3, 3, dotColor);
            }
        }
        else
        {
            PixelFont.DrawText(buf, bx + 4, by + 16, "NO ACCOUNT", ColorSlate, PixelFontSize.Small);
            buf.FillRect(bx + 4, by + 26, bw - 8, 8, ColorSlateDark);
            PixelFont.DrawText(buf, bx + 8, by + 28, "STANDBY MODE", ColorSlate, PixelFontSize.Small);
            PixelFont.DrawText(buf, bx + 4, by + 40, "0 MANAGED", ColorUtil.Darken(ColorSlate, 0.25f), PixelFontSize.Small);
        }
    }

    private void DrawWeatherWindow(PixelBuffer buf, EffectiveWeather weather, float timeSeconds)
    {
        const int wx = 122;
        const int wy = 12;
        const int ww = 140;
        const int wh = 56;

        // Outer window frame
        buf.FillRect(wx - 1, wy - 1, ww + 2, wh + 2, ColorOutline);
        buf.FillRect(wx, wy, ww, wh, _palette.WindowFrame);
        buf.FillRect(wx, wy, ww, 1, ColorUtil.Lighten(_palette.WindowFrame, 0.2f));

        int gx = wx + 3;
        int gy = wy + 3;
        int gw = ww - 6;
        int gh = wh - 6;

        // Sky gradient based on weather
        (int skyTop, int skyBot) = weather switch
        {
            EffectiveWeather.Dawn => (ColorUtil.FromRgb(49, 46, 129), _palette.WeatherDawn),
            EffectiveWeather.Day => (ColorUtil.FromRgb(29, 78, 168), _palette.WeatherDay),
            EffectiveWeather.Sunset => (ColorUtil.FromRgb(59, 23, 84), _palette.WeatherSunset),
            EffectiveWeather.MidnightRain => (ColorUtil.FromRgb(8, 13, 28), _palette.WeatherMidnightRain),
            EffectiveWeather.StormAlert => (ColorUtil.FromRgb(18, 16, 28), _palette.WeatherStormAlert),
            _ => (ColorUtil.FromRgb(29, 78, 168), _palette.WeatherDay)
        };

        buf.FillVerticalGradient(gx, gy, gw, gh, skyTop, skyBot);

        // Subtle storm lightning flash
        bool lightningFlash = weather == EffectiveWeather.StormAlert && ((int)(timeSeconds * 3.5f) % 13 == 0);
        if (lightningFlash)
        {
            buf.BlendRect(gx, gy, gw, gh, ColorUtil.FromRgb(224, 242, 254), 75);
        }

        // Celestial bodies & clouds
        switch (weather)
        {
            case EffectiveWeather.Dawn:
                buf.DrawCircle(gx + 34, gy + 28, 7, ColorUtil.FromRgb(254, 240, 138), true);
                buf.BlendRect(gx + 16, gy + 25, 36, 6, ColorUtil.FromRgb(253, 186, 116), 70);
                DrawPixelCloud(buf, gx + ((int)(timeSeconds * 4f + 60) % (gw + 24)) - 12, gy + 9, ColorUtil.FromRgb(254, 215, 170), gx, gx + gw);
                break;

            case EffectiveWeather.Day:
                buf.DrawCircle(gx + 108, gy + 12, 6, ColorUtil.FromRgb(254, 249, 195), true);
                buf.DrawCircle(gx + 108, gy + 12, 9, ColorUtil.WithAlpha(ColorUtil.FromRgb(254, 240, 138), 55), true);
                DrawPixelCloud(buf, gx + ((int)(timeSeconds * 5f + 12) % (gw + 28)) - 14, gy + 8, ColorWhite, gx, gx + gw);
                DrawPixelCloud(buf, gx + ((int)(timeSeconds * 3f + 78) % (gw + 28)) - 14, gy + 16, ColorUtil.FromRgb(226, 232, 240), gx, gx + gw);
                break;

            case EffectiveWeather.Sunset:
                buf.DrawCircle(gx + 67, gy + 28, 9, ColorUtil.FromRgb(251, 146, 60), true);
                buf.DrawCircle(gx + 67, gy + 28, 6, ColorUtil.FromRgb(254, 240, 138), true);
                DrawPixelCloud(buf, gx + ((int)(timeSeconds * 3.5f + 22) % (gw + 28)) - 14, gy + 10, ColorUtil.FromRgb(253, 164, 175), gx, gx + gw);
                break;

            case EffectiveWeather.MidnightRain:
                // Stars & crescent moon
                DrawStars(buf, gx, gy, gw, 22, timeSeconds);
                buf.DrawCircle(gx + 106, gy + 12, 5, ColorUtil.FromRgb(254, 243, 199), true);
                buf.DrawCircle(gx + 108, gy + 11, 4, skyTop, true);
                break;

            case EffectiveWeather.StormAlert:
                DrawPixelCloud(buf, gx + 14, gy + 6, ColorUtil.FromRgb(51, 65, 85), gx, gx + gw);
                DrawPixelCloud(buf, gx + 54, gy + 4, ColorUtil.FromRgb(30, 41, 59), gx, gx + gw);
                DrawPixelCloud(buf, gx + 96, gy + 7, ColorUtil.FromRgb(51, 65, 85), gx, gx + gw);
                if (lightningFlash)
                {
                    buf.DrawLine(gx + 82, gy + 10, gx + 76, gy + 21, ColorWhite);
                    buf.DrawLine(gx + 76, gy + 21, gx + 80, gy + 21, ColorWhite);
                    buf.DrawLine(gx + 80, gy + 21, gx + 73, gy + 34, ColorUtil.FromRgb(186, 230, 253));
                }
                break;
        }

        // Theme-specific Skyline Silhouette
        bool nightLit = weather is EffectiveWeather.MidnightRain or EffectiveWeather.StormAlert or EffectiveWeather.Sunset;
        switch (_palette.SkylineStyle)
        {
            case "cyber":
                DrawCyberSkyline(buf, gx, gy, gw, gh, nightLit, timeSeconds);
                break;
            case "lofi":
                DrawLofiSkyline(buf, gx, gy, gw, gh, nightLit);
                break;
            default:
                DrawKonohaSkyline(buf, gx, gy, gw, gh, nightLit);
                break;
        }

        // Rain streaks clipped cleanly inside window glass
        if (weather is EffectiveWeather.MidnightRain or EffectiveWeather.StormAlert)
        {
            int dropCount = weather == EffectiveWeather.StormAlert ? 28 : 16;
            int rainColor = weather == EffectiveWeather.StormAlert
                ? ColorUtil.WithAlpha(ColorUtil.FromRgb(203, 213, 225), 175)
                : ColorUtil.WithAlpha(ColorUtil.FromRgb(147, 197, 253), 145);

            for (int r = 0; r < dropCount; r++)
            {
                int rx = gx + ((r * 23 + (int)(timeSeconds * 35f)) % gw);
                int ry = gy + ((r * 17 + (int)(timeSeconds * 75f)) % (gh - 4));
                buf.BlendPixel(rx, ry, rainColor);
                if (rx - 1 >= gx) buf.BlendPixel(rx - 1, ry + 2, rainColor);
                if (weather == EffectiveWeather.StormAlert && rx - 2 >= gx && ry + 4 < gy + gh)
                {
                    buf.BlendPixel(rx - 2, ry + 4, rainColor);
                }
            }
        }

        // Window muntin crossbars & subtle glass reflection
        int muntin = ColorUtil.Darken(_palette.WindowFrame, 0.12f);
        buf.FillRect(gx + gw / 2 - 1, gy, 2, gh, muntin);
        buf.FillRect(gx, gy + gh - 2, gw, 2, ColorUtil.Lighten(_palette.WindowFrame, 0.15f));
        buf.DrawLine(gx + 6, gy + 4, gx + 14, gy + 12, ColorUtil.WithAlpha(ColorWhite, 38));
        buf.DrawLine(gx + 10, gy + 4, gx + 15, gy + 9, ColorUtil.WithAlpha(ColorWhite, 28));
    }

    private static void DrawKonohaSkyline(PixelBuffer buf, int gx, int gy, int gw, int gh, bool nightLit)
    {
        int bottom = gy + gh;
        int cliff = ColorUtil.FromRgb(30, 34, 48);
        int bldg = ColorUtil.FromRgb(20, 24, 36);
        int roof = ColorUtil.FromRgb(42, 48, 66);
        int litWin = ColorUtil.FromRgb(253, 224, 71);

        // Distant Hokage cliff silhouette
        buf.FillRect(gx, bottom - 22, 46, 22, cliff);
        buf.FillRect(gx + 8, bottom - 26, 30, 4, cliff);
        buf.FillRect(gx + 46, bottom - 17, 28, 17, cliff);

        // Konoha curved-roof pagoda & village rooftops
        buf.FillRect(gx + 60, bottom - 15, 24, 15, bldg);
        buf.FillRect(gx + 57, bottom - 17, 30, 2, roof);
        buf.FillRect(gx + 63, bottom - 22, 18, 5, bldg);
        buf.FillRect(gx + 61, bottom - 24, 22, 2, roof);
        buf.FillRect(gx + 70, bottom - 28, 4, 4, roof);

        // Right rooftop & trees
        buf.FillRect(gx + 92, bottom - 14, 34, 14, bldg);
        buf.FillRect(gx + 89, bottom - 16, 40, 2, roof);
        buf.FillRect(gx + 12, bottom - 12, 28, 12, bldg);

        if (nightLit)
        {
            buf.FillRect(gx + 66, bottom - 12, 2, 2, litWin);
            buf.FillRect(gx + 74, bottom - 12, 2, 2, litWin);
            buf.FillRect(gx + 70, bottom - 20, 3, 2, ColorUtil.FromRgb(251, 146, 60));
            buf.FillRect(gx + 98, bottom - 10, 3, 2, litWin);
            buf.FillRect(gx + 108, bottom - 10, 2, 2, litWin);
            buf.FillRect(gx + 116, bottom - 10, 3, 2, litWin);
            buf.FillRect(gx + 20, bottom - 8, 2, 2, litWin);
        }
    }

    private static void DrawCyberSkyline(PixelBuffer buf, int gx, int gy, int gw, int gh, bool nightLit, float timeSeconds)
    {
        int bottom = gy + gh;
        int towerDark = ColorUtil.FromRgb(12, 18, 34);
        int towerMid = ColorUtil.FromRgb(19, 30, 52);
        int magenta = ColorUtil.FromRgb(236, 72, 153);

        // Stepped Neo-Tokyo skyscrapers
        buf.FillRect(gx + 2, bottom - 24, 16, 24, towerDark);
        buf.FillRect(gx + 20, bottom - 34, 18, 34, towerMid);
        buf.FillRect(gx + 28, bottom - 40, 2, 6, ColorCyan);
        buf.FillRect(gx + 42, bottom - 20, 14, 20, towerDark);
        buf.FillRect(gx + 58, bottom - 30, 20, 30, towerMid);
        buf.FillRect(gx + 82, bottom - 37, 16, 37, towerDark);
        buf.FillRect(gx + 89, bottom - 42, 2, 5, magenta);
        buf.FillRect(gx + 100, bottom - 26, 22, 26, towerMid);
        buf.FillRect(gx + 123, bottom - 18, 11, 18, towerDark);

        // Neon strips & holographic signs
        int blink = ((int)(timeSeconds * 3f) & 1) == 0 ? ColorCyan : magenta;
        buf.FillRect(gx + 22, bottom - 28, 14, 2, ColorCyan);
        buf.FillRect(gx + 61, bottom - 24, 12, 3, magenta);
        buf.FillRect(gx + 103, bottom - 20, 14, 2, blink);

        if (nightLit || true)
        {
            for (int wy = bottom - 20; wy < bottom - 4; wy += 4)
            {
                buf.FillRect(gx + 5, wy, 2, 1, ColorCyan);
                buf.FillRect(gx + 24, wy, 3, 1, ColorUtil.FromRgb(125, 211, 252));
                buf.FillRect(gx + 85, wy, 2, 1, magenta);
                buf.FillRect(gx + 106, wy, 3, 1, ColorCyan);
            }
        }
    }

    private static void DrawLofiSkyline(PixelBuffer buf, int gx, int gy, int gw, int gh, bool nightLit)
    {
        int bottom = gy + gh;
        int house1 = ColorUtil.FromRgb(32, 26, 44);
        int house2 = ColorUtil.FromRgb(42, 33, 54);
        int roof = ColorUtil.FromRgb(64, 44, 58);
        int warmWin = ColorUtil.FromRgb(253, 224, 130);

        // Cozy neighborhood houses with pitched roofs
        DrawPitchedHouse(buf, gx + 4, bottom, 26, 15, house1, roof);
        DrawPitchedHouse(buf, gx + 34, bottom, 28, 19, house2, roof);
        DrawPitchedHouse(buf, gx + 70, bottom, 26, 16, house1, roof);
        DrawPitchedHouse(buf, gx + 100, bottom, 30, 20, house2, roof);

        // Utility power-line silhouette
        buf.DrawLine(gx, bottom - 24, gx + gw - 1, bottom - 21, ColorUtil.FromRgb(24, 20, 34));

        if (nightLit || true)
        {
            buf.FillRect(gx + 11, bottom - 9, 4, 4, warmWin);
            buf.FillRect(gx + 42, bottom - 12, 4, 5, warmWin);
            buf.FillRect(gx + 50, bottom - 12, 4, 5, ColorUtil.FromRgb(251, 146, 60));
            buf.FillRect(gx + 78, bottom - 10, 4, 4, warmWin);
            buf.FillRect(gx + 108, bottom - 13, 5, 5, warmWin);
            buf.FillRect(gx + 118, bottom - 13, 4, 4, warmWin);
        }
    }

    private static void DrawPitchedHouse(PixelBuffer buf, int x, int bottomY, int w, int h, int wallCol, int roofCol)
    {
        buf.FillRect(x, bottomY - h, w, h, wallCol);
        int roofH = 6;
        for (int r = 0; r < roofH; r++)
        {
            int inset = r * 2;
            int span = w + 2 - inset * 2;
            if (span <= 0) break;
            buf.FillRect(x - 1 + inset, bottomY - h - r, span, 1, roofCol);
        }
    }

    private static void DrawPixelCloud(PixelBuffer buf, int x, int y, int color, int minX, int maxX)
    {
        DrawClippedRect(buf, x + 3, y, 10, 3, color, minX, maxX);
        DrawClippedRect(buf, x, y + 2, 16, 4, color, minX, maxX);
    }

    private static void DrawClippedRect(PixelBuffer buf, int x, int y, int w, int h, int color, int minX, int maxX)
    {
        int x0 = Math.Max(x, minX);
        int x1 = Math.Min(x + w, maxX);
        if (x0 < x1) buf.FillRect(x0, y, x1 - x0, h, color);
    }

    private static void DrawStars(PixelBuffer buf, int gx, int gy, int gw, int maxH, float timeSeconds)
    {
        for (int i = 0; i < 12; i++)
        {
            int sx = gx + ((i * 29 + 7) % (gw - 24));
            int sy = gy + ((i * 11 + 3) % maxH);
            int alpha = (((int)(timeSeconds * 3f) + i) % 4 == 0) ? 235 : 130;
            buf.BlendPixel(sx, sy, ColorWhite, alpha);
        }
    }

    private void DrawBuildCiBoard(PixelBuffer buf, HqState state, float timeSeconds)
    {
        const int bx = 272;
        const int by = 12;
        const int bw = 100;
        const int bh = 54;

        buf.DrawPixelCard(bx, by, bw, bh, _palette.BoardBg, _palette.Border, ColorUtil.WithAlpha(ColorWhite, 35));

        // Header strip + local clock HH:mm
        buf.FillRect(bx + 1, by + 1, bw - 2, 10, ColorUtil.Lighten(_palette.BoardBg, 0.08f));
        PixelFont.DrawText(buf, bx + 4, by + 4, "BUILD / CI", _palette.Accent, PixelFontSize.Small);

        DateTimeOffset ts = state.Timestamp == default ? DateTimeOffset.Now : state.Timestamp;
        DateTime local = ts.ToLocalTime().DateTime;
        FormatClock(local.Hour, local.Minute, _fmtScratch);
        int clkW = PixelFont.MeasureWidth(_fmtScratch.AsSpan(0, 5), PixelFontSize.Small);
        PixelFont.DrawText(buf, bx + bw - clkW - 4, by + 4, _fmtScratch.AsSpan(0, 5), _palette.Text, PixelFontSize.Small);
        buf.FillRect(bx + 2, by + 11, bw - 4, 1, _palette.Border);

        // Determine CI build status
        BuildInfo? primary = state.Builds.Count > 0 ? state.Builds[0] : null;
        string statusText = "IDLE";
        int lanternColor = ColorSlate;
        int lanternDark = ColorSlateDark;
        bool flash = false;

        if (primary != null)
        {
            if (primary.IsFailing)
            {
                statusText = "FAIL";
                lanternColor = ColorCrimson;
                lanternDark = ColorCrimsonDark;
                flash = ((int)(timeSeconds * 6f) & 1) == 0;
            }
            else if (primary.IsSuccessful)
            {
                statusText = "PASS";
                lanternColor = ColorEmerald;
                lanternDark = ColorEmeraldDark;
            }
            else if (string.Equals(primary.Status, "in_progress", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(primary.Status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                statusText = "WAIT";
                lanternColor = ColorAmber;
                lanternDark = ColorAmberDark;
            }
        }
        else if (state.Mood == HqMood.Failing)
        {
            statusText = "FAIL";
            lanternColor = ColorCrimson;
            lanternDark = ColorCrimsonDark;
            flash = ((int)(timeSeconds * 6f) & 1) == 0;
        }
        else if (state.Mood == HqMood.Alert)
        {
            statusText = "WAIT";
            lanternColor = ColorAmber;
            lanternDark = ColorAmberDark;
        }
        else if (state.Mood == HqMood.Celebrating)
        {
            statusText = "PASS";
            lanternColor = ColorEmerald;
            lanternDark = ColorEmeraldDark;
        }

        // Glowing pixel status lantern
        int lx = bx + 6;
        int ly = by + 16;
        int glowAlpha = flash ? 120 : 60;
        buf.BlendRect(lx - 2, ly - 2, 13, 13, lanternColor, glowAlpha);
        buf.FillRect(lx, ly, 9, 9, ColorOutline);
        buf.FillRect(lx + 1, ly + 1, 7, 7, lanternDark);
        buf.FillRect(lx + 2, ly + 2, 5, 5, flash ? ColorUtil.Lighten(lanternColor, 0.35f) : lanternColor);
        buf.SetPixel(lx + 3, ly + 3, ColorWhite);

        // Crisp 5x7 status label beside lantern
        PixelFont.DrawText(buf, lx + 13, ly + 1, statusText, lanternColor, PixelFontSize.Regular);

        // Active repo & branch
        string repo = primary?.Repo ?? _cachedFirstAgentRepo ?? "KAGEDESK";
        string branch;
        if (primary != null)
        {
            if (_cachedBranchText == null || _cachedPrimaryBranch != primary.Branch || _cachedPrimaryWorkflow != primary.WorkflowName)
            {
                _cachedPrimaryBranch = primary.Branch;
                _cachedPrimaryWorkflow = primary.WorkflowName;
                _cachedBranchText = $"{primary.Branch} \u2022 {primary.WorkflowName}";
            }
            branch = _cachedBranchText;
        }
        else
        {
            branch = "STANDBY \u2022 READY";
        }
        PixelFont.DrawTextTruncated(buf, bx + 4, by + 32, bw - 8, repo, _palette.Text, PixelFontSize.Small);
        PixelFont.DrawTextTruncated(buf, bx + 4, by + 41, bw - 8, branch, ColorSlate, PixelFontSize.Small);
    }

    private static string? FindFirstAgentRepo(HqState state)
    {
        for (int i = 0; i < state.Agents.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(state.Agents[i].Repo))
            {
                return state.Agents[i].Repo;
            }
        }
        return null;
    }

    private void DrawThemeProps(PixelBuffer buf, float timeSeconds, bool hasExtraRow)
    {
        switch (_palette.SkylineStyle)
        {
            case "cyber":
                DrawServerRack(buf, 10, 72, timeSeconds, 0);
                DrawServerRack(buf, 356, 72, timeSeconds, 3);
                break;

            case "lofi":
                DrawPottedPlant(buf, 12, 76);
                DrawWarmDeskLamp(buf, 356, 80, timeSeconds);
                // Tiny sleeping pixel cat on the rug when lower rug area is clear
                if (!hasExtraRow)
                {
                    DrawSleepingCat(buf, 192, 180, timeSeconds);
                }
                break;

            default:
                // Shinobi HQ: Bonsai plant + decorative kanji scroll + warm floor lantern
                DrawBonsaiPlant(buf, 12, 75);
                DrawHangingKanjiScroll(buf, 356, 72);
                break;
        }
    }

    private static void DrawBonsaiPlant(PixelBuffer buf, int x, int y)
    {
        int pot = ColorUtil.FromRgb(154, 52, 18);
        int potHi = ColorUtil.FromRgb(194, 65, 12);
        int trunk = ColorUtil.FromRgb(92, 58, 33);
        int leafDark = ColorUtil.FromRgb(22, 101, 52);
        int leafMid = ColorUtil.FromRgb(34, 139, 68);
        int leafHi = ColorUtil.FromRgb(74, 222, 128);

        // Foliage pads
        buf.FillRect(x + 1, y, 12, 5, leafDark);
        buf.FillRect(x + 2, y + 1, 9, 3, leafMid);
        buf.FillRect(x + 3, y + 1, 4, 1, leafHi);
        buf.FillRect(x + 8, y + 4, 7, 4, leafDark);
        buf.FillRect(x + 9, y + 5, 5, 2, leafMid);
        // Gnarled trunk
        buf.FillRect(x + 6, y + 5, 2, 7, trunk);
        buf.FillRect(x + 7, y + 7, 3, 2, trunk);
        // Ceramic pot
        buf.FillRect(x + 2, y + 11, 10, 6, ColorOutline);
        buf.FillRect(x + 3, y + 12, 8, 4, pot);
        buf.FillRect(x + 2, y + 11, 10, 2, potHi);
    }

    private static void DrawHangingKanjiScroll(PixelBuffer buf, int x, int y)
    {
        int wood = ColorUtil.FromRgb(92, 60, 36);
        int paper = ColorUtil.FromRgb(242, 230, 200);
        int ink = ColorUtil.FromRgb(28, 30, 40);
        int seal = ColorUtil.FromRgb(220, 38, 38);

        // Wooden scroll frame & parchment
        buf.FillRect(x, y, 16, 20, ColorOutline);
        buf.FillRect(x - 1, y, 18, 2, wood);
        buf.FillRect(x - 1, y + 18, 18, 2, wood);
        buf.FillRect(x + 2, y + 2, 12, 16, paper);

        // Stylized pixel kanji strokes ("Kage" / shadow emblem)
        buf.FillRect(x + 5, y + 4, 6, 1, ink);
        buf.FillRect(x + 7, y + 4, 2, 5, ink);
        buf.FillRect(x + 4, y + 7, 8, 1, ink);
        buf.FillRect(x + 5, y + 10, 6, 1, ink);
        buf.FillRect(x + 6, y + 11, 1, 4, ink);
        buf.FillRect(x + 9, y + 11, 1, 4, ink);
        // Red hanko seal stamp
        buf.FillRect(x + 10, y + 14, 2, 2, seal);
    }

    private static void DrawServerRack(PixelBuffer buf, int x, int y, float timeSeconds, int seed)
    {
        int metal = ColorUtil.FromRgb(24, 34, 52);
        int trim = ColorUtil.FromRgb(51, 65, 85);

        buf.FillRect(x, y, 18, 24, ColorOutline);
        buf.FillRect(x + 1, y + 1, 16, 22, metal);
        buf.FillRect(x + 1, y + 1, 16, 1, trim);

        for (int bay = 0; bay < 4; bay++)
        {
            int by = y + 3 + bay * 5;
            buf.FillRect(x + 2, by, 14, 4, ColorUtil.FromRgb(12, 18, 30));
            bool on = (((int)(timeSeconds * 4f) + bay + seed) % 3) != 0;
            int ledCol = (bay & 1) == 0 ? ColorCyan : ColorEmerald;
            buf.FillRect(x + 4, by + 1, 2, 2, on ? ledCol : ColorSlateDark);
            buf.FillRect(x + 8, by + 1, 6, 1, trim);
            if (on) buf.BlendRect(x + 3, by, 4, 4, ledCol, 55);
        }
    }

    private static void DrawPottedPlant(PixelBuffer buf, int x, int y)
    {
        int pot = ColorUtil.FromRgb(180, 83, 9);
        int leaf1 = ColorUtil.FromRgb(21, 128, 61);
        int leaf2 = ColorUtil.FromRgb(74, 222, 128);

        buf.FillRect(x + 5, y + 1, 4, 9, leaf1);
        buf.FillRect(x + 2, y + 3, 4, 6, leaf1);
        buf.FillRect(x + 8, y + 2, 4, 7, leaf2);
        buf.FillRect(x + 6, y, 2, 4, leaf2);
        buf.FillRect(x + 3, y + 10, 8, 7, ColorOutline);
        buf.FillRect(x + 4, y + 11, 6, 5, pot);
    }

    private static void DrawWarmDeskLamp(PixelBuffer buf, int x, int y, float timeSeconds)
    {
        int shade = ColorUtil.FromRgb(245, 158, 11);
        int bulb = ColorUtil.FromRgb(254, 249, 195);
        int stand = ColorUtil.FromRgb(120, 90, 62);

        // Soft warm amber radial glow (kept strictly below baseboard)
        int pulse = 34 + (int)(MathF.Sin(timeSeconds * 2.5f) * 8f);
        buf.DrawCircle(x + 8, y + 6, 11, ColorUtil.WithAlpha(shade, pulse), true);
        buf.DrawCircle(x + 8, y + 6, 7, ColorUtil.WithAlpha(bulb, pulse + 16), true);

        // Lamp shade & stand
        buf.FillRect(x + 3, y + 2, 10, 6, ColorOutline);
        buf.FillRect(x + 4, y + 3, 8, 4, shade);
        buf.FillRect(x + 5, y + 6, 6, 1, bulb);
        buf.FillRect(x + 7, y + 8, 2, 9, stand);
        buf.FillRect(x + 4, y + 16, 8, 2, stand);
    }

    private static void DrawSleepingCat(PixelBuffer buf, int cx, int y, float timeSeconds)
    {
        int ginger = ColorUtil.FromRgb(234, 138, 62);
        int stripe = ColorUtil.FromRgb(180, 83, 24);
        int cream = ColorUtil.FromRgb(254, 243, 199);

        int breath = ((int)(timeSeconds * 1.4f) & 1);
        // Curled sleeping body
        buf.FillRect(cx - 7, y - breath, 14, 7 + breath, ColorOutline);
        buf.FillRect(cx - 6, y + 1 - breath, 12, 5 + breath, ginger);
        buf.FillRect(cx - 3, y + 1 - breath, 2, 3, stripe);
        buf.FillRect(cx + 1, y + 1 - breath, 2, 3, stripe);
        // Ears & sleepy head on left
        buf.SetPixel(cx - 6, y - 1 - breath, ginger);
        buf.SetPixel(cx - 3, y - 1 - breath, ginger);
        buf.FillRect(cx - 5, y + 3, 3, 2, cream);
        // Curled tail on right
        buf.FillRect(cx + 6, y + 3, 3, 2, ginger);

        // Tiny floating Z
        int zPhase = (int)(timeSeconds * 2f) % 4;
        if (zPhase < 3)
        {
            PixelFont.DrawText(buf, cx - 10, y - 7 - zPhase, "Z", ColorUtil.FromRgb(253, 230, 138), PixelFontSize.Small);
        }
    }

    private void UpdateStationParticles(float dt, int extraCount, int coreDeskY, int extraDeskY, HqState state)
    {
        // Trigger Ninja Poof burst whenever clones newly appear or CloneSpawned event is active
        bool cloneEvent = false;
        for (int i = 0; i < state.PendingVisualEvents.Count; i++)
        {
            if (string.Equals(state.PendingVisualEvents[i].Type, "CloneSpawned", StringComparison.OrdinalIgnoreCase))
            {
                cloneEvent = true;
                break;
            }
        }

        if (extraCount > _lastCloneCount || (cloneEvent && _particles.ActiveCount < 8))
        {
            for (int i = 0; i < extraCount; i++)
            {
                int cx = GetExtraStationCenterX(i, extraCount);
                _particles.EmitNinjaPoof(cx, extraDeskY - 6, 12);
            }
        }
        _lastCloneCount = extraCount;

        _emitAccum += dt;
        if (_emitAccum >= 0.14f)
        {
            _emitAccum = 0f;
            for (int i = 0; i < 4; i++)
            {
                EmitForSlot(ref _coreSlots[i], CoreStationCentersX[i], coreDeskY);
            }
            for (int i = 0; i < extraCount; i++)
            {
                EmitForSlot(ref _extraSlots[i], GetExtraStationCenterX(i, extraCount), extraDeskY);
            }
        }

        _particles.Update(dt);
    }

    private void EmitForSlot(ref StationSlot slot, int cx, int deskY)
    {
        switch (slot.State)
        {
            case AgentState.Working:
                if (slot.SpriteKey == "minato")
                {
                    _particles.EmitSparks(cx, deskY - 12, 2);
                }
                else if (slot.SpriteKey == "tsunade")
                {
                    _particles.EmitChakraBubble(cx + 9, deskY - 9);
                }
                break;

            case AgentState.Done:
                _particles.EmitConfetti(cx, deskY - 22, 4);
                break;

            case AgentState.Failed:
                _particles.EmitFireAndSmoke(cx, deskY - 2);
                break;

            case AgentState.Idle:
                if (slot.SpriteKey == "shikamaru")
                {
                    _particles.EmitCoffeeSteam(cx + 16, deskY - 5);
                }
                break;
        }
    }

    public readonly record struct NameplateLayout(
        int TotalWidth,
        int Height,
        int X,
        int Y,
        int NameAvailableWidth,
        int BadgeX,
        int BadgeWidth,
        int BadgeHeight
    )
    {
        public int Left => X;
        public int Right => X + TotalWidth;
    }

    internal static NameplateLayout ComputeNameplateLayout(
        int cx,
        int y,
        ReadOnlySpan<char> nameSpan,
        ReadOnlySpan<char> badgeSpan,
        int maxPlateWidth)
    {
        int badgeW = PixelFont.MeasureWidth(badgeSpan, PixelFontSize.Small) + 4;
        int nameW = PixelFont.MeasureWidth(nameSpan, PixelFontSize.Small) + 5;
        int totalW = Math.Min(maxPlateWidth, nameW + badgeW + 3);
        int x = cx - totalW / 2;
        int h = 11;
        int nameAvail = Math.Max(0, totalW - badgeW - 5);
        int bx = x + totalW - badgeW - 1;
        return new NameplateLayout(totalW, h, x, y, nameAvail, bx, badgeW, h - 2);
    }

    private void DrawWorkstation(PixelBuffer buf, ref StationSlot slot, int cx, int deskY, float timeSeconds, int phase)
    {
        AgentActionKind actionKind = slot.ActionKind;

        // Character sits behind desk with full face & upper outfit clearly visible above desk & monitors
        ShinobiSpriteRenderer.DrawCharacter(
            buf,
            slot.SpriteKey,
            slot.State,
            cx,
            deskY + 3,
            timeSeconds,
            phaseOffset: phase,
            behindDesk: true,
            variantSeed: slot.VariantSeed,
            actionKind: actionKind);

        // Desk & interactive station props
        ShinobiSpriteRenderer.DrawDesk(
            buf,
            slot.StationType,
            cx,
            deskY,
            _palette.DeskTop,
            _palette.DeskFront,
            slot.State,
            timeSeconds,
            actionKind: actionKind);

        // Floating status bubble above head (for alert/done/fail)
        int bubbleY = deskY - 22;
        if (slot.State == AgentState.NeedsApproval)
        {
            int pulseFill = ((int)(timeSeconds * 5f) & 1) == 0 ? ColorAmber : ColorAmberBright;
            ShinobiSpriteRenderer.DrawStatusBubble(buf, cx, bubbleY, "!", pulseFill, ColorOutline, ColorOutline);
        }
        else if (slot.State == AgentState.Done)
        {
            ShinobiSpriteRenderer.DrawStatusBubble(buf, cx, bubbleY, "OK", ColorEmeraldDark, ColorEmerald, ColorEmerald);
        }
        else if (slot.State == AgentState.Failed)
        {
            ShinobiSpriteRenderer.DrawStatusBubble(buf, cx, bubbleY, "!!", ColorCrimsonDark, ColorCrimson, ColorCrimson);
        }

        // Crisp nameplate & tool/state badge strictly below desk (y = deskY + 20 .. deskY + 31)
        DrawNameplateAndBadge(buf, ref slot);
    }

    private void DrawNameplateAndBadge(PixelBuffer buf, ref StationSlot slot)
    {
        ReadOnlySpan<char> badge = (slot.BadgeLabel ?? "IDLE").AsSpan();
        ReadOnlySpan<char> nameSpan = slot.Name.AsSpan();
        if (nameSpan.Length > 10) nameSpan = nameSpan.Slice(0, 10);

        ref readonly var layout = ref slot.Layout;

        buf.DrawPixelCard(layout.X, layout.Y, layout.TotalWidth, layout.Height, _palette.BoardBg, slot.BorderCol);

        PixelFont.DrawTextTruncated(buf, layout.X + 3, layout.Y + 3, layout.NameAvailableWidth, nameSpan, _palette.Text, PixelFontSize.Small);

        buf.FillRect(layout.BadgeX, layout.Y + 1, layout.BadgeWidth, layout.BadgeHeight, slot.BadgeBg);
        PixelFont.DrawText(buf, layout.BadgeX + 2, layout.Y + 3, badge, slot.BadgeFg, PixelFontSize.Small);
    }

    private static void DrawApprovalSpotlight(PixelBuffer buf, int focusX, int focusY, float timeSeconds)
    {
        // Gently dim outer corners with subtle amber-dark tint, leaving alerting station spotlighted
        int dimTint = ColorUtil.FromRgb(22, 14, 8);
        int dimAlpha = 48;

        if (focusX > 0)
        {
            int spotLeft = Math.Max(0, focusX - 46);
            int spotRight = Math.Min(CanvasWidth, focusX + 46);
            int spotTop = Math.Max(0, focusY - 36);
            int spotBottom = Math.Min(CanvasHeight, focusY + 36);

            if (spotTop > 0) buf.BlendRect(0, 0, CanvasWidth, spotTop, dimTint, dimAlpha);
            if (spotBottom < CanvasHeight) buf.BlendRect(0, spotBottom, CanvasWidth, CanvasHeight - spotBottom, dimTint, dimAlpha);
            if (spotLeft > 0) buf.BlendRect(0, spotTop, spotLeft, spotBottom - spotTop, dimTint, dimAlpha);
            if (spotRight < CanvasWidth) buf.BlendRect(spotRight, spotTop, CanvasWidth - spotRight, spotBottom - spotTop, dimTint, dimAlpha);

            // Soft warm spotlight wash over the alerting station
            buf.DrawCircle(focusX, focusY + 4, 34, ColorUtil.WithAlpha(ColorAmberBright, 26), true);
        }

        // Pulsing amber pixel border around the entire HQ room
        int borderAlpha = 185 + (int)(MathF.Sin(timeSeconds * 6f) * 65f);
        int borderCol = ColorUtil.WithAlpha(ColorAmber, borderAlpha);
        buf.DrawOutlineRect(0, 0, CanvasWidth, CanvasHeight, borderCol, thickness: 2);
        buf.DrawOutlineRect(2, 2, CanvasWidth - 4, CanvasHeight - 4, ColorUtil.WithAlpha(ColorAmberBright, borderAlpha / 2), thickness: 1);

        // Corner accent brackets
        buf.FillRect(0, 0, 12, 3, ColorAmberBright);
        buf.FillRect(0, 0, 3, 12, ColorAmberBright);
        buf.FillRect(CanvasWidth - 12, 0, 12, 3, ColorAmberBright);
        buf.FillRect(CanvasWidth - 3, 0, 3, 12, ColorAmberBright);
        buf.FillRect(0, CanvasHeight - 3, 12, 3, ColorAmberBright);
        buf.FillRect(0, CanvasHeight - 12, 3, 12, ColorAmberBright);
        buf.FillRect(CanvasWidth - 12, CanvasHeight - 3, 12, 3, ColorAmberBright);
        buf.FillRect(CanvasWidth - 3, CanvasHeight - 12, 3, 12, ColorAmberBright);
    }

    private static string ShortenDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "AGENT";
        int parenIdx = name.IndexOf('(');
        if (parenIdx > 0)
        {
            name = name[..parenIdx];
        }
        return name.Trim();
    }

    private static void FormatClock(int hour, int minute, char[] dest)
    {
        dest[0] = (char)('0' + (hour / 10));
        dest[1] = (char)('0' + (hour % 10));
        dest[2] = ':';
        dest[3] = (char)('0' + (minute / 10));
        dest[4] = (char)('0' + (minute % 10));
    }

    private static int HashString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int h = 17;
        for (int i = 0; i < s.Length; i++)
        {
            h = h * 31 + s[i];
        }
        return h;
    }
}
