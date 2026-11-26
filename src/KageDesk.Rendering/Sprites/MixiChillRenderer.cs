using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;

namespace KageDesk.Rendering.Sprites;

public enum MixiChillActivity
{
    EatingNoodles = 0,
    WatchingYouTube = 1,
    Gaming = 2,
    StreamingTalk = 3,
    SingingKaraoke = 4
}

/// <summary>
/// Renders the exclusive All-Idle Do Mixi Gaming 2-monitor chill corner diorama
/// (sleeveless black tank top / ao ba lo, signature cheek mole, styled spiky hair,
/// red/black gaming headset, side-angled cross-legged pose on a racing gaming chair,
/// and 5 cycling chill activities) for both Wallpaper HQ and Taskbar Pet Overlay.
/// </summary>
public static class MixiChillRenderer
{
    public const float ActivityDurationSeconds = 8.0f;

    private static readonly int Outline = ColorUtil.FromRgb(14, 16, 24);
    private static readonly int SkinBase = ColorUtil.FromRgb(245, 206, 168);
    private static readonly int SkinShade = ColorUtil.FromRgb(216, 164, 120);
    private static readonly int SkinHighlight = ColorUtil.FromRgb(255, 228, 202);
    private static readonly int MoleDark = ColorUtil.FromRgb(16, 10, 14);
    private static readonly int EyeDark = ColorUtil.FromRgb(18, 20, 30);
    private static readonly int EyeWhite = ColorUtil.FromRgb(248, 250, 252);
    private static readonly int HairDark = ColorUtil.FromRgb(20, 22, 32);
    private static readonly int HairMid = ColorUtil.FromRgb(42, 48, 66);
    private static readonly int HairHi = ColorUtil.FromRgb(84, 96, 122);
    private static readonly int TankBlack = ColorUtil.FromRgb(22, 24, 34);
    private static readonly int TankShade = ColorUtil.FromRgb(12, 14, 22);
    private static readonly int ShortsDark = ColorUtil.FromRgb(30, 36, 52);
    private static readonly int ShortsShade = ColorUtil.FromRgb(18, 22, 34);
    private static readonly int Crimson = ColorUtil.FromRgb(220, 38, 38);
    private static readonly int CrimsonBright = ColorUtil.FromRgb(248, 113, 113);
    private static readonly int CrimsonDark = ColorUtil.FromRgb(127, 29, 29);
    private static readonly int ChairBlack = ColorUtil.FromRgb(24, 26, 36);
    private static readonly int ChairHighlight = ColorUtil.FromRgb(48, 54, 72);
    private static readonly int DeskDark = ColorUtil.FromRgb(34, 38, 52);
    private static readonly int DeskTop = ColorUtil.FromRgb(50, 56, 74);
    private static readonly int DeskLeg = ColorUtil.FromRgb(20, 24, 34);
    private static readonly int BezelDark = ColorUtil.FromRgb(24, 28, 40);
    private static readonly int ScreenBg = ColorUtil.FromRgb(14, 18, 30);
    private static readonly int Cyan = ColorUtil.FromRgb(34, 211, 238);
    private static readonly int Gold = ColorUtil.FromRgb(250, 204, 21);
    private static readonly int GoldLight = ColorUtil.FromRgb(254, 240, 138);
    private static readonly int Emerald = ColorUtil.FromRgb(34, 197, 94);
    private static readonly int Magenta = ColorUtil.FromRgb(236, 72, 153);
    private static readonly int Silver = ColorUtil.FromRgb(203, 213, 225);

    public static MixiChillActivity GetActivityForTime(float timeSeconds)
    {
        float clamped = MathF.Max(0f, timeSeconds);
        int index = ((int)(clamped / ActivityDurationSeconds)) % 5;
        return (MixiChillActivity)index;
    }

    public static string GetShortBadge(MixiChillActivity activity) => activity switch
    {
        MixiChillActivity.EatingNoodles => "AN MI",
        MixiChillActivity.WatchingYouTube => "YOUTUBE",
        MixiChillActivity.Gaming => "GAME",
        MixiChillActivity.StreamingTalk => "STREAM",
        MixiChillActivity.SingingKaraoke => "KARAOKE",
        _ => "CHILL"
    };

    public static string GetFullBadge(MixiChillActivity activity) => activity switch
    {
        MixiChillActivity.EatingNoodles => "DO MIXI \u00B7 AN MI",
        MixiChillActivity.WatchingYouTube => "DO MIXI \u00B7 YOUTUBE",
        MixiChillActivity.Gaming => "DO MIXI \u00B7 GAME",
        MixiChillActivity.StreamingTalk => "DO MIXI \u00B7 STREAM",
        MixiChillActivity.SingingKaraoke => "DO MIXI \u00B7 KARAOKE",
        _ => "DO MIXI \u00B7 CHILL"
    };

    /// <summary>
    /// Strict exclusive All-Idle check for Do Mixi Gaming:
    /// Returns true ONLY when DemoScenario == "mixi" OR (showMixiWhenAllIdle == true AND zero agents/bots
    /// are non-idle AND no handoff is pending or active).
    /// </summary>
    public static bool ShouldRenderMixiChillCorner(
        HqState state,
        bool showMixiWhenAllIdle,
        bool hasActiveHandoff = false,
        long lastHandledHandoffSeq = -1)
    {
        if (string.Equals(state.DemoScenario, "mixi", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!showMixiWhenAllIdle || hasActiveHandoff)
        {
            return false;
        }

        var agents = state.Agents;
        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i].State != AgentState.Idle)
            {
                return false;
            }
        }

        var events = state.PendingVisualEvents;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].SequenceId > lastHandledHandoffSeq &&
                string.Equals(events[i].Type, "handoff", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Draws the complete Do Mixi Gaming 2-monitor streamer chill corner anchored at (cx, groundY).
    /// Footprint spans approximately [cx - 40 .. cx + 38] horizontally and [groundY - 46 .. groundY] vertically.
    /// </summary>
    public static MixiChillActivity DrawChillCorner(
        PixelBuffer buf,
        int cx,
        int groundY,
        float timeSeconds,
        MixiChillActivity? activityOverride = null)
    {
        MixiChillActivity activity = activityOverride ?? GetActivityForTime(timeSeconds);
        bool wearsHeadset = activity is MixiChillActivity.Gaming or MixiChillActivity.StreamingTalk;

        // 0. Soft ground shadow + subtle RGB streamer ambient underglow
        int rgbGlow = GetRgbCycle(timeSeconds, 0);
        buf.BlendRect(cx - 39, groundY - 2, 75, 3, ColorUtil.FromRgb(8, 10, 18), 115);
        buf.BlendRect(cx - 38, groundY - 12, 43, 4, rgbGlow, 34);

        // 1. Dual-Monitor Streamer Desk (left/center-left)
        DrawDualMonitorDesk(buf, cx, groundY, timeSeconds, activity, headsetOnDesk: !wearsHeadset);

        // 2. Tall Black & Crimson Racing Gaming Chair (right, angled 3/4 facing monitors)
        DrawRacingGamingChair(buf, cx, groundY);

        // 3. Do Mixi Gaming Character seated 3/4 cross-legged (ngoi xeo bat chan len ghe)
        DrawMixiCharacter(buf, cx, groundY, timeSeconds, activity, wearsHeadset);

        // 4. Boom Mic Arm (in foreground between desk and Mixi so it reads cleanly)
        DrawBoomMicArm(buf, cx, groundY, timeSeconds, activity);

        // 5. Activity Foreground FX (steam, music notes, speech waves, gaming sparks)
        DrawActivityEffects(buf, cx, groundY, timeSeconds, activity);

        return activity;
    }

    private static void DrawDualMonitorDesk(
        PixelBuffer buf,
        int cx,
        int groundY,
        float timeSeconds,
        MixiChillActivity activity,
        bool headsetOnDesk)
    {
        int deskLeft = cx - 40;
        int deskTopY = groundY - 15;
        int deskW = 47;

        // Desk legs
        buf.FillRect(deskLeft + 2, deskTopY + 4, 3, 11, Outline);
        buf.FillRect(deskLeft + 3, deskTopY + 4, 1, 10, DeskLeg);
        buf.FillRect(deskLeft + deskW - 5, deskTopY + 4, 3, 11, Outline);
        buf.FillRect(deskLeft + deskW - 4, deskTopY + 4, 1, 10, DeskLeg);
        // Back crossbar + glowing PC tower under left side of desk
        buf.FillRect(deskLeft + 5, deskTopY + 5, 8, 9, Outline);
        buf.FillRect(deskLeft + 6, deskTopY + 6, 6, 7, DeskLeg);
        buf.FillRect(deskLeft + 7, deskTopY + 7, 1, 5, GetRgbCycle(timeSeconds, 2));
        buf.FillRect(deskLeft + 9, deskTopY + 8, 2, 2, Crimson);

        // Desk tabletop + RGB strip edge
        buf.FillRect(deskLeft, deskTopY - 1, deskW, 6, Outline);
        buf.FillRect(deskLeft + 1, deskTopY, deskW - 2, 2, DeskTop);
        buf.FillRect(deskLeft + 1, deskTopY + 2, deskW - 2, 2, DeskDark);
        for (int rx = 0; rx < deskW - 4; rx += 4)
        {
            buf.FillRect(deskLeft + 2 + rx, deskTopY + 3, Math.Min(3, deskW - 4 - rx), 1, GetRgbCycle(timeSeconds, rx / 4));
        }

        // === MONITOR 2 (Angled Secondary Side Monitor — Left) ===
        int m2x = cx - 40;
        int m2y = groundY - 31;
        // Stand
        buf.FillRect(m2x + 6, m2y + 13, 3, 3, Outline);
        buf.FillRect(m2x + 4, deskTopY - 1, 7, 2, BezelDark);
        // Bezel & screen (15x13 outer, 11x9 inner)
        buf.FillRect(m2x, m2y, 15, 13, Outline);
        buf.FillRect(m2x + 1, m2y + 1, 13, 11, BezelDark);
        buf.FillRect(m2x + 2, m2y + 2, 11, 9, ScreenBg);
        DrawSecondaryMonitorContent(buf, m2x + 2, m2y + 2, 11, 9, timeSeconds, activity);

        // === MONITOR 1 (Main Wide Monitor — Center) ===
        int m1x = cx - 23;
        int m1y = groundY - 32;
        // Stand
        buf.FillRect(m1x + 10, m1y + 14, 3, 3, Outline);
        buf.FillRect(m1x + 7, deskTopY - 1, 9, 2, BezelDark);
        // Bezel & screen (22x14 outer, 18x10 inner)
        buf.FillRect(m1x, m1y, 22, 14, Outline);
        buf.FillRect(m1x + 1, m1y + 1, 20, 12, BezelDark);
        buf.FillRect(m1x + 2, m1y + 2, 18, 10, ScreenBg);
        DrawMainMonitorContent(buf, m1x + 2, m1y + 2, 18, 10, timeSeconds, activity);
        // Power LED
        buf.SetPixel(m1x + 19, m1y + 12, activity == MixiChillActivity.StreamingTalk ? Crimson : Cyan);

        // Mechanical RGB Keyboard + Gaming Mouse
        int kbX = cx - 22;
        int kbY = deskTopY - 2;
        buf.FillRect(kbX, kbY, 15, 3, Outline);
        buf.FillRect(kbX + 1, kbY + 1, 13, 1, ColorUtil.FromRgb(40, 46, 66));
        for (int kx = 0; kx < 12; kx += 3)
        {
            buf.SetPixel(kbX + 1 + kx, kbY + 1, GetRgbCycle(timeSeconds, kx));
        }

        // Mousepad & gaming mouse
        buf.FillRect(cx - 5, kbY, 5, 3, Outline);
        buf.FillRect(cx - 4, kbY + 1, 3, 1, CrimsonBright);

        // Resting Red/Black Gaming Headset on open desk surface (when Mixi is NOT wearing it!)
        if (headsetOnDesk)
        {
            int hx = cx - 33;
            int hy = deskTopY - 6;
            // Arch band resting upright on desk
            buf.FillRect(hx, hy, 7, 2, Outline);
            buf.FillRect(hx + 1, hy, 5, 1, Crimson);
            buf.SetPixel(hx + 3, hy, CrimsonBright);
            // Left & right crimson-accented over-ear cups resting cleanly on desk
            buf.FillRect(hx - 1, hy + 2, 3, 4, Outline);
            buf.FillRect(hx, hy + 2, 1, 3, ChairBlack);
            buf.FillRect(hx, hy + 3, 1, 2, Crimson);
            buf.SetPixel(hx, hy + 3, CrimsonBright);
            buf.FillRect(hx + 5, hy + 2, 3, 4, Outline);
            buf.FillRect(hx + 6, hy + 2, 1, 3, ChairBlack);
            buf.FillRect(hx + 6, hy + 3, 1, 2, Crimson);
            buf.SetPixel(hx + 6, hy + 3, CrimsonBright);
        }
    }

    private static void DrawMainMonitorContent(
        PixelBuffer buf,
        int x,
        int y,
        int w,
        int h,
        float timeSeconds,
        MixiChillActivity activity)
    {
        switch (activity)
        {
            case MixiChillActivity.EatingNoodles:
            case MixiChillActivity.WatchingYouTube:
            {
                // Iconic YouTube Video Player UI
                buf.FillRect(x, y, w, 2, ColorUtil.FromRgb(28, 30, 42));
                buf.FillRect(x + 1, y, 4, 1, Crimson); // Red YT header badge
                // Video viewport
                buf.FillRect(x + 1, y + 2, w - 2, 6, ColorUtil.FromRgb(26, 36, 58));
                // Red YouTube rounded play button in center (iconic & instantly recognizable)
                int py = y + 3;
                int px = x + 6;
                buf.FillRect(px, py, 6, 4, Crimson);
                // White play triangle inside red button
                buf.SetPixel(px + 2, py + 1, EyeWhite);
                buf.SetPixel(px + 2, py + 2, EyeWhite);
                buf.SetPixel(px + 3, py + 1, EyeWhite);
                // Animated red YouTube playback progress bar at bottom
                buf.FillRect(x + 1, y + 9, w - 2, 1, ColorUtil.FromRgb(64, 72, 92));
                int prog = 2 + ((int)(timeSeconds * 3f) % (w - 4));
                buf.FillRect(x + 1, y + 9, prog, 1, Crimson);
                buf.SetPixel(x + 1 + prog, y + 9, EyeWhite);
                break;
            }

            case MixiChillActivity.Gaming:
            {
                // Dynamic colorful FPS / action game HUD
                int phase = (int)(timeSeconds * 8f) % 4;
                buf.FillRect(x, y, w, 5, ColorUtil.FromRgb(24, 52, 86)); // Game sky
                buf.FillRect(x, y + 5, w, 5, ColorUtil.FromRgb(30, 48, 38)); // Ground
                // Moving explosion / action flash
                int fx = x + 4 + (phase * 3);
                buf.FillRect(fx, y + 3, 3, 3, phase % 2 == 0 ? Gold : ColorUtil.FromRgb(249, 115, 22));
                buf.SetPixel(fx + 1, y + 4, EyeWhite);
                // Crosshair
                buf.SetPixel(x + 9, y + 4, Cyan);
                buf.SetPixel(x + 8, y + 4, Cyan);
                buf.SetPixel(x + 10, y + 4, Cyan);
                // Health & ammo HUD bars
                buf.FillRect(x + 1, y + 8, 6, 1, Emerald);
                buf.FillRect(x + 11, y + 8, 5, 1, Gold);
                break;
            }

            case MixiChillActivity.StreamingTalk:
            {
                // OBS Stream Scene + blinking red LIVE dot + audio EQ
                buf.FillRect(x, y, w, h, ColorUtil.FromRgb(20, 24, 40));
                bool liveBlink = ((int)(timeSeconds * 3f) & 1) == 0;
                buf.FillRect(x + 1, y + 1, 3, 2, liveBlink ? Crimson : CrimsonDark);
                buf.FillRect(x + 5, y + 1, 6, 1, EyeWhite);
                // Mini facecam preview box
                buf.FillRect(x + 2, y + 4, 7, 5, ColorUtil.FromRgb(34, 40, 60));
                buf.FillRect(x + 4, y + 5, 3, 2, SkinBase);
                buf.FillRect(x + 4, y + 7, 3, 2, TankBlack);
                // Live voice meter bars on right
                for (int b = 0; b < 4; b++)
                {
                    int bh = 1 + (((int)(timeSeconds * 10f) + b * 2) % 5);
                    int barCol = bh >= 4 ? Gold : Emerald;
                    buf.FillRect(x + 11 + b * 2, y + 9 - bh, 1, bh, barCol);
                }
                break;
            }

            case MixiChillActivity.SingingKaraoke:
            {
                // Vibrant purple karaoke stage + sweeping lyric highlight bars
                buf.FillRect(x, y, w, h, ColorUtil.FromRgb(36, 16, 62));
                int sweep = ((int)(timeSeconds * 7f)) % (w - 2);
                // Top lyric line with gold/cyan sweep
                buf.FillRect(x + 1, y + 2, w - 2, 2, EyeWhite);
                buf.FillRect(x + 1, y + 2, sweep, 2, Gold);
                // Second lyric line
                buf.FillRect(x + 3, y + 5, w - 6, 1, ColorUtil.FromRgb(203, 213, 225));
                // Bouncing bottom music visualizer
                for (int i = 0; i < 8; i++)
                {
                    int vh = 1 + (((int)(timeSeconds * 9f) + i) % 3);
                    buf.FillRect(x + 1 + i * 2, y + h - vh, 1, vh, (i & 1) == 0 ? Magenta : Cyan);
                }
                break;
            }
        }
    }

    private static void DrawSecondaryMonitorContent(
        PixelBuffer buf,
        int x,
        int y,
        int w,
        int h,
        float timeSeconds,
        MixiChillActivity activity)
    {
        int scroll = (int)(timeSeconds * 4f);
        switch (activity)
        {
            case MixiChillActivity.EatingNoodles:
            case MixiChillActivity.WatchingYouTube:
            {
                // Scrolling YouTube comments / suggested videos
                for (int row = 0; row < 4; row++)
                {
                    int ry = y + 1 + row * 2;
                    int idx = (row + scroll) % 4;
                    int avatarCol = idx switch { 0 => CrimsonBright, 1 => Cyan, 2 => Gold, _ => Emerald };
                    buf.SetPixel(x + 1, ry, avatarCol);
                    buf.FillRect(x + 3, ry, 5 + (idx % 3), 1, ColorUtil.FromRgb(180, 192, 210));
                }
                break;
            }

            case MixiChillActivity.Gaming:
            {
                // Tactical radar minimap + scoreboard
                buf.FillRect(x + 1, y + 1, 5, 5, ColorUtil.FromRgb(12, 46, 36));
                buf.SetPixel(x + 3, y + 3, Emerald);
                int blip = scroll % 3;
                buf.SetPixel(x + 2 + blip, y + 2, CrimsonBright);
                buf.FillRect(x + 7, y + 1, 3, 1, Cyan);
                buf.FillRect(x + 7, y + 3, 3, 1, Gold);
                buf.FillRect(x + 1, y + 7, 9, 1, ColorUtil.FromRgb(56, 189, 248));
                break;
            }

            case MixiChillActivity.StreamingTalk:
            {
                // Vibrant scrolling stream chat lines (Twitch/YouTube colorful usernames + chat)
                for (int row = 0; row < 4; row++)
                {
                    int ry = y + 1 + row * 2;
                    int idx = (row + scroll) % 5;
                    int userCol = idx switch
                    {
                        0 => Cyan,
                        1 => Magenta,
                        2 => Emerald,
                        3 => Gold,
                        _ => CrimsonBright
                    };
                    buf.FillRect(x + 1, ry, 2, 1, userCol);
                    buf.FillRect(x + 4, ry, 4 + (idx % 3), 1, EyeWhite);
                }
                break;
            }

            case MixiChillActivity.SingingKaraoke:
            {
                // Karaoke queue & sweeping lyric preview
                buf.FillRect(x, y, w, h, ColorUtil.FromRgb(30, 14, 52));
                int sweep = scroll % (w - 2);
                buf.FillRect(x + 1, y + 2, w - 2, 1, Cyan);
                buf.FillRect(x + 1, y + 2, sweep, 1, Magenta);
                buf.FillRect(x + 1, y + 5, w - 3, 1, Gold);
                buf.FillRect(x + 1, y + 7, w - 4, 1, EyeWhite);
                break;
            }
        }
    }

    private static void DrawRacingGamingChair(PixelBuffer buf, int cx, int groundY)
    {
        // Tall black-and-red racing gaming chair positioned on the right, angled 3/4 facing left
        int seatX = cx + 10;
        int seatY = groundY - 13;

        // 5-star wheel base & hydraulic piston
        buf.FillRect(cx + 21, groundY - 9, 3, 6, Outline);
        buf.FillRect(cx + 22, groundY - 9, 1, 5, Silver);
        buf.FillRect(cx + 13, groundY - 3, 19, 2, Outline);
        buf.FillRect(cx + 14, groundY - 3, 17, 1, ChairHighlight);
        // Caster wheels
        buf.FillRect(cx + 13, groundY - 2, 3, 2, ChairBlack);
        buf.FillRect(cx + 21, groundY - 2, 3, 2, ChairBlack);
        buf.FillRect(cx + 29, groundY - 2, 3, 2, ChairBlack);

        // Tall racing backrest (angled slightly back on the right: cx+24 .. cx+36)
        int backX = cx + 24;
        int backTop = groundY - 39;
        buf.FillRect(backX, backTop, 13, 28, Outline);
        buf.FillRect(backX + 1, backTop + 1, 11, 26, ChairBlack);
        // Signature crimson racing wing bolsters
        buf.FillRect(backX + 1, backTop + 3, 2, 22, Crimson);
        buf.FillRect(backX + 10, backTop + 3, 2, 22, Crimson);
        buf.SetPixel(backX + 1, backTop + 4, CrimsonBright);
        buf.SetPixel(backX + 11, backTop + 4, CrimsonBright);
        // Center racing seam + headrest neck pillow
        buf.FillRect(backX + 3, backTop + 1, 7, 4, ChairHighlight);
        buf.FillRect(backX + 4, backTop + 2, 5, 2, Crimson);
        // Twin racing harness cutout accents
        buf.FillRect(backX + 4, backTop + 7, 2, 2, Outline);
        buf.FillRect(backX + 7, backTop + 7, 2, 2, Outline);

        // Seat cushion pan (wide enough for side-angled cross-legged pose!)
        buf.FillRect(seatX, seatY, 22, 5, Outline);
        buf.FillRect(seatX + 1, seatY + 1, 20, 3, ChairBlack);
        buf.FillRect(seatX + 2, seatY + 3, 18, 1, Crimson); // Crimson racing trim along seat edge

        // Back armrest support
        buf.FillRect(cx + 23, seatY - 5, 8, 2, Outline);
        buf.FillRect(cx + 24, seatY - 5, 6, 1, ChairHighlight);
    }

    private static void DrawMixiCharacter(
        PixelBuffer buf,
        int cx,
        int groundY,
        float timeSeconds,
        MixiChillActivity activity,
        bool wearsHeadset)
    {
        // Motion offsets by activity
        int leanX = 0;
        int bobY = 0;
        bool isLaughing = false;
        bool mouthOpen = false;

        switch (activity)
        {
            case MixiChillActivity.EatingNoodles:
                bobY = ((int)(timeSeconds * 3f) & 1);
                mouthOpen = ((int)(timeSeconds * 4f) % 4) is 1 or 2;
                break;

            case MixiChillActivity.WatchingYouTube:
                float laughPhase = timeSeconds % 3.2f;
                isLaughing = laughPhase > 2.2f;
                bobY = isLaughing ? -((int)(timeSeconds * 9f) & 1) : ((int)(timeSeconds * 1.5f) & 1);
                mouthOpen = isLaughing;
                break;

            case MixiChillActivity.Gaming:
                leanX = -2;
                bobY = ((int)(timeSeconds * 7f) & 1);
                break;

            case MixiChillActivity.StreamingTalk:
                leanX = -1;
                bobY = ((int)(timeSeconds * 2.5f) & 1);
                mouthOpen = ((int)(timeSeconds * 6f) & 1) == 0;
                break;

            case MixiChillActivity.SingingKaraoke:
                int swayStep = (int)(timeSeconds * 3.5f) % 4;
                leanX = swayStep switch { 1 => -1, 3 => 1, _ => 0 };
                bobY = (swayStep & 1) == 1 ? -1 : 0;
                mouthOpen = ((int)(timeSeconds * 5f) % 3) != 0;
                break;
        }

        int bx = cx + 19 + leanX; // Body anchor on gaming chair
        int seatSurfaceY = groundY - 13;

        // === 1. TUCKED CROSS-LEGGED LOWER BODY (ngoi xeo bat chan len ghe) ===
        // Hip & dark casual shorts sitting angled on chair cushion
        buf.FillRect(bx - 5, seatSurfaceY - 4, 12, 5, Outline);
        buf.FillRect(bx - 4, seatSurfaceY - 3, 10, 3, ShortsDark);
        buf.FillRect(bx - 2, seatSurfaceY - 1, 7, 1, ShortsShade);

        // Raised bent knee jutting forward + bare leg/foot tucked horizontally UP onto the chair cushion!
        // (Clearly NOT dangling down to the floor -> unmistakable "bat chan len ghe" silhouette!)
        buf.FillRect(bx - 9, seatSurfaceY - 6, 6, 5, Outline);
        buf.FillRect(bx - 8, seatSurfaceY - 5, 4, 3, ShortsDark); // Raised shorts knee
        // Bare lower shin/calf & foot folded horizontally across the seat cushion
        buf.FillRect(bx - 8, seatSurfaceY - 2, 9, 3, Outline);
        buf.FillRect(bx - 7, seatSurfaceY - 1, 7, 2, SkinBase);
        buf.FillRect(bx - 3, seatSurfaceY, 4, 1, SkinShade);
        buf.SetPixel(bx - 7, seatSurfaceY - 1, SkinHighlight); // Knee skin highlight
        buf.FillRect(bx + 1, seatSurfaceY - 2, 3, 2, SkinShade); // Tucked foot resting on seat

        // === 2. TORSO — ICONIC BLACK SLEEVELESS TANK TOP (AO BA LO) ===
        int torsoTop = seatSurfaceY - 13 + bobY;
        buf.FillRect(bx - 4, torsoTop - 1, 11, 10, Outline);
        // Tank top core in dark black/charcoal
        buf.FillRect(bx - 3, torsoTop, 9, 8, TankBlack);
        buf.FillRect(bx - 3, torsoTop + 6, 9, 2, TankShade);
        // Wide bare skin neckline & exposed bare skin shoulders (defining ao ba lo silhouette!)
        buf.FillRect(bx - 1, torsoTop, 4, 2, SkinBase);
        buf.SetPixel(bx, torsoTop, SkinHighlight);
        // Exposed bare left/right shoulders outside the narrow tank straps
        buf.FillRect(bx - 4, torsoTop, 2, 3, SkinBase);
        buf.FillRect(bx + 5, torsoTop, 2, 3, SkinBase);
        buf.SetPixel(bx + 5, torsoTop, SkinHighlight);
        // Narrow dark tank straps clearly separating neck from bare shoulders
        buf.FillRect(bx - 2, torsoTop, 1, 2, TankBlack);
        buf.FillRect(bx + 3, torsoTop, 2, 2, TankBlack);
        // Subtle crimson Mixi emblem on tank chest
        buf.FillRect(bx - 1, torsoTop + 3, 3, 2, Crimson);
        buf.SetPixel(bx, torsoTop + 3, CrimsonBright);

        // === 3. HEAD (3/4 turned toward monitors, hair, eyes, mouth, SIGNATURE MOLE) ===
        int hx = bx + 1;
        int headTop = torsoTop - 10;

        // Head outline & warm skin face (12x10 outer, 10x8 face)
        buf.FillRect(hx - 6, headTop - 1, 12, 10, Outline);
        buf.FillRect(hx - 5, headTop, 10, 8, SkinBase);
        buf.FillRect(hx - 5, headTop + 7, 10, 1, SkinShade);
        // Bright warm cheek highlight on the visible right-front cheek (behind mole)
        buf.FillRect(hx + 1, headTop + 4, 3, 3, SkinHighlight);

        // Eyes (looking slightly left toward the 2 monitors)
        int eyeY = headTop + 3;
        if (isLaughing)
        {
            // Happy ^ ^ laughing eyes
            buf.SetPixel(hx - 4, eyeY + 1, EyeDark);
            buf.SetPixel(hx - 3, eyeY, EyeDark);
            buf.SetPixel(hx - 2, eyeY + 1, EyeDark);
            buf.SetPixel(hx - 1, eyeY + 1, EyeDark);
            buf.SetPixel(hx, eyeY, EyeDark);
            buf.SetPixel(hx + 1, eyeY + 1, EyeDark);
        }
        else if (activity == MixiChillActivity.Gaming)
        {
            // Focused intense gamer eyes + brow
            buf.FillRect(hx - 4, eyeY - 1, 3, 1, HairDark);
            buf.FillRect(hx - 1, eyeY - 1, 3, 1, HairDark);
            buf.FillRect(hx - 4, eyeY, 2, 2, EyeDark);
            buf.SetPixel(hx - 4, eyeY, EyeWhite);
            buf.FillRect(hx - 1, eyeY, 2, 2, EyeDark);
            buf.SetPixel(hx - 1, eyeY, EyeWhite);
        }
        else
        {
            // Warm lively eyes facing 3/4 left
            buf.FillRect(hx - 4, eyeY, 2, 2, EyeDark);
            buf.SetPixel(hx - 4, eyeY, EyeWhite);
            buf.FillRect(hx - 1, eyeY, 2, 2, EyeDark);
            buf.SetPixel(hx - 1, eyeY, EyeWhite);
        }

        // Mouth expression
        if (activity == MixiChillActivity.SingingKaraoke && mouthOpen)
        {
            buf.FillRect(hx - 3, headTop + 6, 3, 2, ColorUtil.FromRgb(153, 27, 27));
            buf.SetPixel(hx - 2, headTop + 6, CrimsonBright);
        }
        else if (mouthOpen)
        {
            buf.FillRect(hx - 3, headTop + 6, 3, 1, ColorUtil.FromRgb(140, 42, 42));
            buf.SetPixel(hx - 2, headTop + 6, EyeWhite);
        }
        else
        {
            buf.FillRect(hx - 3, headTop + 6, 2, 1, SkinShade);
        }

        // === UNMISTAKABLE SIGNATURE FACIAL MOLE (MUT RUOI) ===
        // Placed on bright cheek at (hx + 2, headTop + 5), cleanly separated from eyes (eyeY=3) and mouth (hx-3..-1, y=6)!
        buf.SetPixel(hx + 2, headTop + 5, MoleDark);

        // Styled short black spiky/swept-up undercut hair (toc vuot dung)
        buf.FillRect(hx - 6, headTop - 3, 12, 4, Outline);
        buf.FillRect(hx - 5, headTop - 5, 3, 2, Outline); // Front spike
        buf.FillRect(hx - 1, headTop - 6, 4, 3, Outline); // Center swept-up peak
        buf.FillRect(hx + 3, headTop - 4, 3, 2, Outline); // Back crest

        buf.FillRect(hx - 5, headTop - 2, 10, 3, HairDark);
        buf.FillRect(hx - 4, headTop - 4, 2, 2, HairDark);
        buf.FillRect(hx, headTop - 5, 2, 3, HairMid);
        buf.FillRect(hx + 3, headTop - 3, 2, 2, HairDark);
        // Crisp cool-slate styling shine (clearly hair, never confused with headset band)
        buf.FillRect(hx - 3, headTop - 3, 2, 1, HairHi);
        buf.SetPixel(hx + 1, headTop - 4, HairHi);
        // Clean short faded undercut at back/side
        buf.FillRect(hx + 4, headTop + 1, 1, 4, HairMid);

        // === RED & BLACK GAMING HEADSET (worn ONLY during Gaming & StreamingTalk!) ===
        if (wearsHeadset)
        {
            // Bold crimson-and-black crown headband arching clearly over hair
            buf.FillRect(hx - 3, headTop - 6, 8, 2, Outline);
            buf.FillRect(hx - 2, headTop - 5, 6, 1, Crimson);
            buf.FillRect(hx - 1, headTop - 5, 2, 1, CrimsonBright);
            buf.FillRect(hx + 4, headTop - 4, 2, 6, Outline);
            buf.FillRect(hx + 4, headTop - 3, 1, 4, ChairBlack);
            // Bold crimson-and-black over-ear gaming earcup on visible right ear
            buf.FillRect(hx + 3, headTop + 1, 4, 6, Outline);
            buf.FillRect(hx + 4, headTop + 2, 2, 4, ChairBlack);
            buf.FillRect(hx + 4, headTop + 3, 2, 2, Crimson);
            buf.SetPixel(hx + 5, headTop + 3, CrimsonBright);
        }
        else
        {
            // Bare ear cleanly visible when headset is resting on desk!
            buf.FillRect(hx + 4, headTop + 3, 2, 3, SkinBase);
            buf.SetPixel(hx + 5, headTop + 4, SkinShade);
        }

        // === 4. ACTIVITY-SPECIFIC BARE ARMS & PROPS ===
        DrawActivityArmsAndProps(buf, bx, torsoTop, hx, headTop, seatSurfaceY, timeSeconds, activity);
    }

    private static void DrawActivityArmsAndProps(
        PixelBuffer buf,
        int bx,
        int torsoTop,
        int hx,
        int headTop,
        int seatSurfaceY,
        float timeSeconds,
        MixiChillActivity activity)
    {
        switch (activity)
        {
            case MixiChillActivity.EatingNoodles:
            {
                // Ceramic steaming noodle bowl (to mi) held on left side
                int bowlX = bx - 12;
                int bowlY = torsoTop + 3;
                buf.FillRect(bowlX, bowlY, 9, 6, Outline);
                buf.FillRect(bowlX + 1, bowlY + 2, 7, 3, EyeWhite);
                buf.FillRect(bowlX + 1, bowlY + 3, 7, 1, Crimson); // Classic red noodle bowl rim stripe
                // Golden noodles & garnish inside bowl
                buf.FillRect(bowlX + 1, bowlY + 1, 7, 1, Gold);
                buf.SetPixel(bowlX + 3, bowlY + 1, Emerald); // Scallion green
                buf.SetPixel(bowlX + 5, bowlY + 1, CrimsonBright); // Chili/shrimp

                // Left bare arm supporting bowl
                buf.FillRect(bx - 6, torsoTop + 3, 4, 3, SkinBase);
                buf.SetPixel(bx - 5, torsoTop + 5, SkinShade);

                // Animated bare right arm + wooden chopsticks lifting golden noodles to mouth!
                int liftCycle = (int)(timeSeconds * 4f) % 4; // 0: dip, 1: lift mid, 2: at mouth, 3: chew
                int chopY = liftCycle switch { 0 => bowlY, 1 => headTop + 7, _ => headTop + 5 };
                int chopX = liftCycle switch { 0 => bowlX + 4, 1 => bx - 6, _ => hx - 5 };

                // Bare right arm reaching forward from sleeveless shoulder
                buf.FillRect(bx + 2, torsoTop + 1, 4, 4, Outline);
                buf.FillRect(bx + 3, torsoTop + 1, 3, 3, SkinBase);
                buf.FillRect(bx - 1, torsoTop + 2, 4, 2, SkinBase);
                // Wooden chopsticks
                int wood = ColorUtil.FromRgb(180, 120, 58);
                buf.DrawLine(bx - 1, torsoTop + 2, chopX, chopY, wood);
                buf.DrawLine(bx - 1, torsoTop + 3, chopX, chopY + 1, wood);
                // Dangling golden wavy noodles on chopsticks!
                if (liftCycle is 1 or 2)
                {
                    buf.FillRect(chopX - 1, chopY + 1, 2, 3, Gold);
                    buf.SetPixel(chopX, chopY + 2, GoldLight);
                }
                break;
            }

            case MixiChillActivity.WatchingYouTube:
            {
                // Bare right arm propped up with hand resting on chin (chong cam)
                buf.FillRect(bx + 2, torsoTop + 1, 4, 5, Outline);
                buf.FillRect(bx + 3, torsoTop + 1, 2, 4, SkinBase);
                buf.FillRect(bx - 1, torsoTop - 1, 4, 3, SkinBase); // Forearm angled up to chin
                buf.FillRect(hx - 2, headTop + 7, 3, 2, SkinHighlight); // Hand propping chin
                // Left bare arm relaxed on tucked knee
                buf.FillRect(bx - 6, torsoTop + 3, 4, 3, SkinBase);
                buf.SetPixel(bx - 6, torsoTop + 5, SkinShade);
                break;
            }

            case MixiChillActivity.Gaming:
            {
                // Both bare arms extended fast onto keyboard & mouse with rapid typing jitter!
                int tap = ((int)(timeSeconds * 14f) & 1);
                // Left bare arm on keyboard
                buf.FillRect(bx - 10, torsoTop + 4 + tap, 7, 3, Outline);
                buf.FillRect(bx - 9, torsoTop + 5 + tap, 6, 2, SkinBase);
                // Right bare arm on gaming mouse
                buf.FillRect(bx - 4, torsoTop + 2, 8, 3, Outline);
                buf.FillRect(bx - 3, torsoTop + 2 + (1 - tap), 6, 2, SkinBase);
                buf.SetPixel(bx + 3, torsoTop + 2, SkinHighlight);
                break;
            }

            case MixiChillActivity.StreamingTalk:
            {
                // Animated expressive streamer hand gestures talking into boom mic!
                int gesture = (int)(timeSeconds * 5f) % 3;
                int gDy = gesture == 1 ? -2 : (gesture == 2 ? -1 : 0);
                // Left expressive bare arm & open hand
                buf.FillRect(bx - 8, torsoTop + 1 + gDy, 6, 3, Outline);
                buf.FillRect(bx - 7, torsoTop + 2 + gDy, 4, 2, SkinBase);
                buf.SetPixel(bx - 8, torsoTop + 1 + gDy, SkinHighlight);
                // Right expressive bare arm gesturing near chest
                buf.FillRect(bx + 2, torsoTop + 1, 4, 5, Outline);
                buf.FillRect(bx + 3, torsoTop + 1, 2, 4, SkinBase);
                buf.FillRect(bx, torsoTop + 3 - gDy, 3, 2, SkinBase);
                break;
            }

            case MixiChillActivity.SingingKaraoke:
            {
                // Bare arm raised holding handheld silver-mesh karaoke microphone near mouth!
                int sway = ((int)(timeSeconds * 3.5f) & 1);
                // Bare right arm raised up toward face
                buf.FillRect(bx + 2, torsoTop + 1, 4, 5, Outline);
                buf.FillRect(bx + 3, torsoTop + 1, 2, 4, SkinBase);
                buf.FillRect(bx - 3, torsoTop - 1 + sway, 6, 3, SkinBase);
                // Handheld karaoke microphone (dark grip + bright silver mesh ball)
                int micX = hx - 6;
                int micY = headTop + 4 + sway;
                buf.FillRect(micX, micY, 4, 4, Outline);
                buf.FillRect(micX + 1, micY + 1, 2, 2, Silver);
                buf.SetPixel(micX + 1, micY + 1, EyeWhite); // Mesh glint
                buf.FillRect(micX + 2, micY + 3, 2, 4, ChairBlack); // Mic handle
                buf.SetPixel(micX + 2, micY + 5, Crimson); // Mic ring accent
                // Left bare arm swaying to rhythm
                buf.FillRect(bx - 8, torsoTop + 2 - sway, 5, 3, SkinBase);
                break;
            }
        }
    }

    private static void DrawBoomMicArm(
        PixelBuffer buf,
        int cx,
        int groundY,
        float timeSeconds,
        MixiChillActivity activity)
    {
        // Articulated matte-black boom arm mounted on right side of desk reaching toward Mixi
        int clampX = cx + 2;
        int clampY = groundY - 15;
        int elbowX = cx + 6;
        int elbowY = groundY - 29;
        int tipX = activity == MixiChillActivity.StreamingTalk ? cx + 12 : cx + 10;
        int tipY = activity == MixiChillActivity.StreamingTalk ? groundY - 28 : groundY - 26;

        buf.FillRect(clampX - 1, clampY - 1, 3, 3, Outline);
        buf.DrawLine(clampX, clampY - 1, elbowX, elbowY, Outline);
        buf.DrawLine(clampX + 1, clampY - 1, elbowX + 1, elbowY, ChairHighlight);
        buf.DrawLine(elbowX, elbowY, tipX, tipY, Outline);
        buf.DrawLine(elbowX, elbowY + 1, tipX, tipY + 1, ChairHighlight);

        // Condenser mic capsule + pop filter
        buf.FillRect(tipX - 1, tipY - 1, 4, 4, Outline);
        buf.FillRect(tipX, tipY, 2, 2, ChairBlack);
        bool micActive = activity == MixiChillActivity.StreamingTalk;
        buf.SetPixel(tipX, tipY, micActive && (((int)(timeSeconds * 6f) & 1) == 0) ? CrimsonBright : Crimson);
    }

    private static void DrawActivityEffects(
        PixelBuffer buf,
        int cx,
        int groundY,
        float timeSeconds,
        MixiChillActivity activity)
    {
        switch (activity)
        {
            case MixiChillActivity.EatingNoodles:
            {
                // Rising hot noodle bowl steam wisps
                int s = (int)(timeSeconds * 5f) % 5;
                int sx = cx + 10;
                int sy = groundY - 24;
                buf.BlendPixel(sx, sy - s, EyeWhite, 195);
                buf.BlendPixel(sx + 1, sy - 1 - s, EyeWhite, 155);
                buf.BlendPixel(sx + 3, sy - ((s + 2) % 5), EyeWhite, 180);
                buf.BlendPixel(sx + 2, sy - 2 - ((s + 2) % 5), GoldLight, 130);
                break;
            }

            case MixiChillActivity.WatchingYouTube:
            {
                // Cheerful laugh sparkle / tiny "HA" pulse when laughing
                if ((timeSeconds % 3.2f) > 2.2f)
                {
                    int ly = groundY - 43 - ((int)(timeSeconds * 6f) & 1);
                    PixelFont.DrawText(buf, cx + 10, ly, "HA", GoldLight, PixelFontSize.Small);
                }
                break;
            }

            case MixiChillActivity.Gaming:
            {
                // Tiny intensity action sparks near keyboard & monitors
                int sp = (int)(timeSeconds * 12f) % 4;
                int sx = sp < 2 ? cx - 14 + sp * 6 : cx - 3 + (sp - 2) * 5;
                int sy = groundY - 19 - (sp & 1) * 3;
                buf.BlendPixel(sx, sy, Gold, 230);
                buf.BlendPixel(sx + 1, sy - 1, Cyan, 210);
                break;
            }

            case MixiChillActivity.StreamingTalk:
            {
                // Pulsing speech/audio wave pixels radiating beside the boom mic!
                int wave = (int)(timeSeconds * 7f) % 3;
                int wx = cx + 15;
                int wy = groundY - 29;
                if (wave >= 0)
                {
                    buf.BlendPixel(wx, wy, Cyan, 220);
                    buf.BlendPixel(wx, wy + 2, Cyan, 220);
                }
                if (wave >= 1)
                {
                    buf.BlendPixel(wx - 2, wy - 1, Gold, 200);
                    buf.BlendPixel(wx - 2, wy + 1, Gold, 200);
                    buf.BlendPixel(wx - 2, wy + 3, Gold, 200);
                }
                if (wave >= 2)
                {
                    buf.BlendPixel(wx - 4, wy - 1, CrimsonBright, 175);
                    buf.BlendPixel(wx - 4, wy + 3, CrimsonBright, 175);
                }
                break;
            }

            case MixiChillActivity.SingingKaraoke:
            {
                // Colorful pixel music notes floating upward above Mixi!
                int rise1 = (int)(timeSeconds * 5f) % 7;
                int rise2 = (int)(timeSeconds * 5f + 3.5f) % 7;
                DrawPixelMusicNote(buf, cx + 11, groundY - 39 - rise1, Magenta);
                DrawPixelMusicNote(buf, cx + 24, groundY - 38 - rise2, Cyan);
                if (((int)(timeSeconds * 4f) & 1) == 0)
                {
                    DrawPixelMusicNote(buf, cx + 17, groundY - 41 - ((rise1 + 2) % 5), Gold);
                }
                break;
            }
        }
    }

    private static void DrawPixelMusicNote(PixelBuffer buf, int x, int y, int color)
    {
        // Crisp 4x5 pixel eighth note
        buf.SetPixel(x, y + 3, color);
        buf.SetPixel(x + 1, y + 3, color);
        buf.SetPixel(x + 1, y + 2, color);
        buf.SetPixel(x + 1, y + 1, color);
        buf.SetPixel(x + 1, y, color);
        buf.SetPixel(x + 2, y, color);
        buf.SetPixel(x + 3, y + 1, color);
    }

    private static int GetRgbCycle(float timeSeconds, int offset)
    {
        int idx = ((int)(timeSeconds * 4f) + offset) % 6;
        return idx switch
        {
            0 => Crimson,
            1 => ColorUtil.FromRgb(249, 115, 22),
            2 => Gold,
            3 => Emerald,
            4 => Cyan,
            _ => Magenta
        };
    }
}
