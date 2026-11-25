using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;

namespace KageDesk.Rendering.Sprites;

public static class ShinobiSpriteRenderer
{
    private static readonly int Outline = ColorUtil.FromRgb(16, 18, 28);
    private static readonly int SkinBase = ColorUtil.FromRgb(245, 206, 168);
    private static readonly int SkinShade = ColorUtil.FromRgb(219, 169, 126);
    private static readonly int SkinHighlight = ColorUtil.FromRgb(255, 226, 198);
    private static readonly int EyeDark = ColorUtil.FromRgb(22, 24, 34);
    private static readonly int EyeWhite = ColorUtil.FromRgb(248, 250, 252);
    private static readonly int SharinganRed = ColorUtil.FromRgb(239, 68, 68);
    private static readonly int SharinganGlow = ColorUtil.FromRgb(254, 178, 178);
    private static readonly int HeadbandMetal = ColorUtil.FromRgb(156, 172, 196);
    private static readonly int HeadbandCloth = ColorUtil.FromRgb(38, 46, 66);
    private static readonly int KonohaVest = ColorUtil.FromRgb(58, 108, 76);
    private static readonly int KonohaVestShade = ColorUtil.FromRgb(38, 74, 52);
    private static readonly int KonohaVestHi = ColorUtil.FromRgb(86, 144, 104);
    private static readonly int ShinobiDark = ColorUtil.FromRgb(34, 40, 56);
    private static readonly int ShinobiPants = ColorUtil.FromRgb(28, 34, 48);
    private static readonly int SandalDark = ColorUtil.FromRgb(20, 24, 34);

    /// <summary>
    /// Draws a complete pixel-art character anchored at (cx, bottomY) where bottomY is the foot baseline.
    /// Character footprint is approx 18x24 pixels.
    /// </summary>
    public static void DrawCharacter(
        PixelBuffer buf,
        string spriteKey,
        AgentState state,
        int cx,
        int bottomY,
        float timeSeconds,
        int phaseOffset = 0,
        bool behindDesk = false,
        int variantSeed = 0,
        AgentActionKind actionKind = AgentActionKind.None,
        bool isWalking = false,
        bool carryingScroll = false,
        bool facingRight = true,
        int behaviorVariant = 0)
    {
        string key = NormalizeSpriteKey(spriteKey);
        int bv = Math.Abs(behaviorVariant) % 3;
        float t = timeSeconds + phaseOffset * 0.43f + bv * 0.19f;

        if (state == AgentState.Working && actionKind == AgentActionKind.None)
        {
            actionKind = AgentActionKind.Coding;
        }

        // Motion & animation offsets
        int jumpY = 0;
        int shakeX = 0;
        int breathY = 0;
        int headBobY = 0;
        bool armsUp = false;
        bool waveAlt = false;
        bool handsOnHead = false;
        bool typingFast = false;
        bool eyesBlink = false;
        bool eyesHappy = false;
        bool eyesPanic = false;
        int eyeScan = 0;

        switch (state)
        {
            case AgentState.Idle:
                if (bv == 2)
                {
                    // Subtle lo-fi head-bob / stretch rhythm
                    breathY = ((int)(t * 2.2f) & 1);
                    headBobY = ((int)(t * 1.1f) % 4 == 0) ? -1 : 0;
                }
                else
                {
                    breathY = ((int)(t * (bv == 1 ? 1.8f : 1.3f)) & 1);
                }
                float blinkCycle = t % (bv == 1 ? 2.9f : 3.6f);
                eyesBlink = blinkCycle > (bv == 1 ? 2.72f : 3.42f);
                break;

            case AgentState.Working:
                switch (actionKind)
                {
                    case AgentActionKind.Reading:
                        typingFast = false;
                        breathY = ((int)(t * 1.5f) & 1);
                        eyeScan = ((int)(t * (3.2f + bv * 0.5f)) % 3) - 1;
                        break;
                    case AgentActionKind.Searching:
                        typingFast = false;
                        breathY = ((int)(t * 3f) & 1);
                        eyeScan = bv == 1 ? (((int)(t * 4f) & 1) == 0 ? -1 : 1) : 0;
                        break;
                    case AgentActionKind.Thinking:
                        typingFast = false;
                        breathY = ((int)(t * 1.5f) & 1);
                        if (bv == 1)
                        {
                            // Variant 1: subtle eye glance left/right while thinking
                            eyeScan = ((int)(t * 2.2f) % 3) - 1;
                        }
                        else if (bv == 2)
                        {
                            // Variant 2: slow contemplative head nod
                            headBobY = ((int)(t * 2.0f) % 3 == 0) ? 1 : 0;
                        }
                        break;
                    case AgentActionKind.GitOps:
                        typingFast = false;
                        breathY = ((int)(t * 2f) & 1);
                        break;
                    case AgentActionKind.Reviewing:
                        typingFast = false;
                        breathY = ((int)(t * 2f) & 1);
                        eyeScan = bv == 2 ? (((int)(t * 3f) % 3) - 1) : 0;
                        break;
                    case AgentActionKind.Executing:
                        typingFast = true;
                        breathY = ((int)(t * (7f + bv)) & 1);
                        break;
                    case AgentActionKind.Testing:
                    case AgentActionKind.Building:
                        typingFast = true;
                        breathY = ((int)(t * (4f + bv)) & 1);
                        break;
                    default: // Coding
                        if (bv == 1)
                        {
                            // Variant 1: focused lean-in with periodic brief sip/pause
                            bool sipPause = (t % 4.2f) > 3.55f;
                            typingFast = !sipPause;
                            breathY = sipPause ? 0 : ((int)(t * 5f) & 1);
                            headBobY = sipPause ? -1 : 0;
                        }
                        else if (bv == 2)
                        {
                            // Variant 2: rhythmic lo-fi head-bob coding
                            typingFast = true;
                            breathY = ((int)(t * 4f) & 1);
                            headBobY = ((int)(t * 3.2f) & 1) == 0 ? 1 : 0;
                        }
                        else
                        {
                            // Variant 0: classic fast typing
                            typingFast = true;
                            breathY = ((int)(t * 6f) & 1);
                        }
                        break;
                }
                break;

            case AgentState.NeedsApproval:
                jumpY = -(int)(MathF.Abs(MathF.Sin(t * 7.2f)) * 4.5f + 0.5f);
                armsUp = true;
                waveAlt = ((int)(t * 8f) & 1) == 0;
                break;

            case AgentState.Done:
                jumpY = -(int)(MathF.Abs(MathF.Sin(t * 6.0f)) * 4.0f + 0.5f);
                armsUp = true;
                eyesHappy = true;
                break;

            case AgentState.Failed:
                shakeX = (((int)(t * 18f) & 1) == 0) ? -1 : 1;
                handsOnHead = true;
                eyesPanic = true;
                break;
        }

        int x = cx + shakeX;
        int footY = bottomY + jumpY;

        // Ground shadow (when standing freely, not hidden behind desk)
        if (!behindDesk)
        {
            int shadowW = jumpY < -1 ? 10 : 14;
            buf.BlendRect(cx - shadowW / 2, bottomY - 1, shadowW, 2, ColorUtil.FromRgb(10, 12, 20), 110);
        }

        // Body coordinates relative to footY
        int legsTop = footY - 5;
        int walkBobY = isWalking ? (((((int)(t * 8f) + phaseOffset) & 1) == 0) ? -1 : 0) : 0;
        int torsoTop = footY - 13 + (jumpY == 0 ? breathY : 0) + walkBobY;
        int headTop = torsoTop - 9 + (jumpY == 0 && !isWalking ? headBobY : 0);

        // 1. Back hair / cape layer (behind torso)
        DrawBackLayer(buf, key, x, headTop, torsoTop, footY, t);

        // 2. Legs & sandals (unless fully behind desk)
        if (!behindDesk || isWalking)
        {
            int leftLegX = x - 4;
            int rightLegX = x + 1;
            int stepOffset = (state == AgentState.Done || state == AgentState.NeedsApproval) && jumpY < -1 ? -1 : 0;
            int pantsColor = key switch
            {
                "astro-dev" => ColorUtil.FromRgb(226, 232, 240),
                "mecha-droid" => ColorUtil.FromRgb(71, 85, 105),
                "mixi" => ColorUtil.FromRgb(20, 22, 30),
                "street-gamer" => ColorUtil.FromRgb(30, 41, 59),
                _ => ShinobiPants
            };
            int shoeColor = key switch
            {
                "mixi" => ColorUtil.FromRgb(185, 28, 28),
                "astro-dev" => ColorUtil.FromRgb(234, 88, 12),
                "street-gamer" => ColorUtil.FromRgb(248, 250, 252),
                "mecha-droid" => ColorUtil.FromRgb(51, 65, 85),
                _ => SandalDark
            };

            if (isWalking)
            {
                int walkStep = ((int)(t * 8f) + phaseOffset) & 3;
                int leftDx = walkStep switch { 0 => -2, 2 => 1, _ => 0 };
                int leftDy = walkStep == 0 ? 1 : 0;
                int rightDx = walkStep switch { 2 => 2, 0 => -1, _ => 0 };
                int rightDy = walkStep == 2 ? 1 : 0;

                buf.FillRect(leftLegX - 1 + leftDx, legsTop, 5, 5 + leftDy, Outline);
                buf.FillRect(rightLegX - 1 + rightDx, legsTop, 5, 5 + rightDy, Outline);
                buf.FillRect(leftLegX + leftDx, legsTop, 3, 3 + leftDy, pantsColor);
                buf.FillRect(rightLegX + rightDx, legsTop, 3, 3 + rightDy, pantsColor);
                buf.FillRect(leftLegX + leftDx, footY - 2 + leftDy, 3, 2, shoeColor);
                buf.FillRect(rightLegX + rightDx, footY - 2 + rightDy, 3, 2, shoeColor);
            }
            else
            {
                buf.FillRect(leftLegX - 1, legsTop, 5, 5 + stepOffset, Outline);
                buf.FillRect(rightLegX - 1, legsTop, 5, 5 + stepOffset, Outline);
                buf.FillRect(leftLegX, legsTop, 3, 3 + stepOffset, pantsColor);
                buf.FillRect(rightLegX, legsTop, 3, 3 + stepOffset, pantsColor);
                buf.FillRect(leftLegX, footY - 2 + stepOffset, 3, 2, shoeColor);
                buf.FillRect(rightLegX, footY - 2 + stepOffset, 3, 2, shoeColor);
            }
        }

        // 3. Torso / Outfit
        DrawTorso(buf, key, x, torsoTop, variantSeed);

        // 4. Arms (idle, typing, waving, victory, panic, walking, carrying scroll)
        if (isWalking && carryingScroll)
        {
            int sleeveColor = ResolveSleeveColor(key, variantSeed);
            int handColor = ResolveHandColor(key);
            int scrollX = facingRight ? x + 3 : x - 10;
            int scrollY = torsoTop + 3;

            // Tied pixel scroll
            buf.FillRect(scrollX, scrollY, 7, 4, ColorUtil.FromRgb(254, 243, 199));
            buf.FillRect(scrollX, scrollY, 1, 4, ColorUtil.FromRgb(255, 255, 255));
            buf.FillRect(scrollX + 6, scrollY, 1, 4, ColorUtil.FromRgb(255, 255, 255));
            buf.FillRect(scrollX + 3, scrollY, 1, 4, ColorUtil.FromRgb(239, 68, 68));
            buf.SetPixel(scrollX + 3, scrollY - 1, ColorUtil.FromRgb(239, 68, 68));
            buf.DrawOutlineRect(scrollX - 1, scrollY - 1, 9, 6, ColorUtil.WithAlpha(ColorUtil.FromRgb(253, 224, 71), 180));

            if (facingRight)
            {
                buf.FillRect(x - 2, torsoTop + 3, 4, 3, sleeveColor);
                buf.FillRect(x + 2, torsoTop + 4, 2, 2, handColor);
            }
            else
            {
                buf.FillRect(x - 2, torsoTop + 3, 4, 3, sleeveColor);
                buf.FillRect(x - 4, torsoTop + 4, 2, 2, handColor);
            }
        }
        else if (isWalking)
        {
            int walkStep = ((int)(t * 8f) + phaseOffset) & 3;
            int sleeveColor = ResolveSleeveColor(key, variantSeed);
            int handColor = ResolveHandColor(key);
            int leftArmDy = walkStep == 0 ? 1 : (walkStep == 2 ? -1 : 0);
            int rightArmDy = walkStep == 2 ? 1 : (walkStep == 0 ? -1 : 0);

            buf.FillRect(x - 7, torsoTop + 2 + leftArmDy, 2, 5, sleeveColor);
            buf.SetPixel(x - 7, torsoTop + 7 + leftArmDy, handColor);

            buf.FillRect(x + 5, torsoTop + 2 + rightArmDy, 2, 5, sleeveColor);
            buf.SetPixel(x + 5, torsoTop + 7 + rightArmDy, handColor);
        }
        else
        {
            DrawArms(buf, key, x, torsoTop, headTop, armsUp, waveAlt, handsOnHead, typingFast, t, state, variantSeed, actionKind);
        }

        // 5. Head, Face, Eyes, Hair
        DrawHeadAndHair(buf, key, x, headTop, state, eyesBlink, eyesHappy, eyesPanic, t, variantSeed, eyeScan, actionKind);

        // 6. Action-specific and character props (with subtle behaviorVariant animations)
        if (state == AgentState.Working)
        {
            switch (actionKind)
            {
                case AgentActionKind.Reading:
                    DrawReadingScroll(buf, x, torsoTop + 2, t);
                    break;
                case AgentActionKind.Searching:
                    DrawMagnifyingGlass(buf, x + 3, torsoTop + 1, t);
                    break;
                case AgentActionKind.Thinking:
                    DrawThoughtVariantProp(buf, x, headTop, t, bv);
                    break;
                case AgentActionKind.GitOps:
                    DrawCommitPacket(buf, x, torsoTop, t);
                    break;
                case AgentActionKind.Coding:
                    DrawCodingSparks(buf, x, torsoTop, t, bv);
                    break;
                case AgentActionKind.Executing:
                case AgentActionKind.Testing:
                case AgentActionKind.Building:
                    DrawExecutionSparks(buf, x, torsoTop, t, bv);
                    break;
            }
        }
        else if (state == AgentState.Idle)
        {
            if (key == "kakashi")
            {
                DrawTinyOrangeBook(buf, x + 4, torsoTop + 1);
            }
            else if (bv == 0 && ((int)(t * 1.4f) % 4) == 0)
            {
                // Subtle occasional resting z puff
                int zy = headTop - 5 - ((int)(t * 3f) & 1);
                buf.BlendPixel(x + 6, zy, ColorUtil.FromRgb(148, 163, 184), 175);
                buf.BlendPixel(x + 7, zy, ColorUtil.FromRgb(148, 163, 184), 175);
                buf.BlendPixel(x + 6, zy + 1, ColorUtil.FromRgb(148, 163, 184), 150);
                buf.BlendPixel(x + 7, zy + 2, ColorUtil.FromRgb(148, 163, 184), 150);
            }
            else if (bv == 1)
            {
                // Subtle rising warm coffee steam wisp on resting variant 1
                int step = (int)(t * 3f) % 3;
                buf.BlendPixel(x + 7, torsoTop - 1 - step, ColorUtil.FromRgb(226, 232, 240), 140);
                buf.BlendPixel(x + 8, torsoTop - 2 - ((step + 1) % 3), ColorUtil.FromRgb(253, 224, 71), 110);
            }
        }

        // 7. Sweat drop on panic/failed
        if (eyesPanic)
        {
            int dropY = headTop + 2 + ((int)(t * 6f) & 1);
            buf.SetPixel(x + 7, dropY, ColorUtil.FromRgb(56, 189, 248));
            buf.SetPixel(x + 7, dropY + 1, ColorUtil.FromRgb(186, 230, 253));
        }

        // 8. Subtle signature aura / LED accents
        if (key == "minato" && state == AgentState.Working)
        {
            int sparkIdx = (int)(t * 10f) % 4;
            int sx = sparkIdx < 2 ? x - 8 : x + 7;
            int sy = headTop + 2 + (sparkIdx & 1) * 5;
            buf.BlendPixel(sx, sy, ColorUtil.FromRgb(254, 240, 138), 220);
            buf.BlendPixel(sx + 1, sy - 1, ColorUtil.FromRgb(56, 189, 248), 190);
        }
        else if (key == "mixi" && state == AgentState.Working)
        {
            // Subtle crimson streamer headset LED pulse
            int pulse = 140 + (int)(MathF.Sin(t * 7f) * 65f);
            buf.BlendPixel(x - 8, headTop + 4, ColorUtil.FromRgb(239, 68, 68), pulse);
            buf.BlendPixel(x + 7, headTop + 4, ColorUtil.FromRgb(239, 68, 68), pulse);
        }
        else if (key == "wizard-dev" && state == AgentState.Working)
        {
            int orbit = (int)(t * 8f) % 4;
            int ox = orbit < 2 ? x - 8 : x + 7;
            int oy = headTop - 1 + (orbit & 1) * 3;
            buf.BlendPixel(ox, oy, ColorUtil.FromRgb(250, 204, 21), 220);
        }
        else if (key == "cyber-hacker" && state == AgentState.Working)
        {
            int pulse = 135 + (int)(MathF.Sin(t * 8f) * 65f);
            buf.BlendPixel(x + 7, headTop + 4, ColorUtil.FromRgb(236, 72, 153), pulse);
        }
        else if (key == "clone")
        {
            int rimAlpha = 95 + (int)(MathF.Sin(t * 5f) * 35f);
            buf.BlendPixel(x - 7, torsoTop + 2, ColorUtil.FromRgb(56, 189, 248), rimAlpha);
            buf.BlendPixel(x + 6, headTop + 4, ColorUtil.FromRgb(56, 189, 248), rimAlpha);
        }
        else if (key == "omp" && state == AgentState.Working)
        {
            int pulse = 130 + (int)(MathF.Sin(t * 7f) * 60f);
            buf.BlendPixel(x - 6, torsoTop + 1, ColorUtil.FromRgb(34, 211, 238), pulse);
        }
        else if (key == "agy" && state == AgentState.Working)
        {
            int pulse = 130 + (int)(MathF.Sin(t * 6f) * 60f);
            buf.BlendPixel(x + 6, torsoTop + 1, ColorUtil.FromRgb(192, 132, 252), pulse);
        }
    }

    private static int ResolveSleeveColor(string key, int variantSeed) => key switch
    {
        "minato" => ColorUtil.FromRgb(241, 245, 249),
        "tsunade" => ColorUtil.FromRgb(77, 139, 91),
        "clone" => ColorUtil.FromRgb(34, 211, 238),
        "omp" => ColorUtil.FromRgb(24, 30, 42),
        "agy" => ColorUtil.FromRgb(49, 46, 129),
        "claude" => ColorUtil.FromRgb(194, 65, 12),
        "codex" => ColorUtil.FromRgb(16, 185, 129),
        "mixi" => ColorUtil.FromRgb(20, 22, 30),
        "neko-dev" => ColorUtil.FromRgb(167, 139, 250),
        "cyber-hacker" => ColorUtil.FromRgb(20, 26, 42),
        "ronin-dev" => ColorUtil.FromRgb(153, 27, 27),
        "astro-dev" => ColorUtil.FromRgb(241, 245, 249),
        "wizard-dev" => ColorUtil.FromRgb(49, 46, 129),
        "mecha-droid" => ColorUtil.FromRgb(148, 163, 184),
        "street-gamer" => ColorUtil.FromRgb(248, 250, 252),
        "generic" => (variantSeed & 1) == 1 ? ColorUtil.FromRgb(124, 58, 237) : ColorUtil.FromRgb(16, 185, 129),
        _ => ShinobiDark
    };

    private static int ResolveHandColor(string key) => key switch
    {
        "clone" => ColorUtil.FromRgb(165, 243, 252),
        "astro-dev" => ColorUtil.FromRgb(249, 115, 22),
        "mecha-droid" => ColorUtil.FromRgb(34, 211, 238),
        _ => SkinBase
    };

    private static void DrawReadingScroll(PixelBuffer buf, int x, int y, float t)
    {
        int parchment = ColorUtil.FromRgb(254, 243, 199);
        int roller = ColorUtil.FromRgb(180, 83, 9);
        int textCyan = ColorUtil.FromRgb(56, 189, 248);

        buf.FillRect(x - 5, y, 11, 6, Outline);
        buf.FillRect(x - 4, y + 1, 9, 4, parchment);
        buf.FillRect(x - 5, y + 1, 1, 4, roller);
        buf.FillRect(x + 5, y + 1, 1, 4, roller);

        int animLine = ((int)(t * 4f) & 1);
        buf.FillRect(x - 3, y + 2, 6, 1, textCyan);
        buf.FillRect(x - 3, y + 3 + animLine, 4, 1, ColorUtil.FromRgb(14, 165, 233));
    }

    private static void DrawMagnifyingGlass(PixelBuffer buf, int x, int y, float t)
    {
        int sweep = (int)(MathF.Sin(t * 4f) * 1.8f);
        int gx = x + sweep;
        int gy = y;

        int rim = ColorUtil.FromRgb(148, 163, 184);
        int lens = ColorUtil.FromRgb(34, 211, 238);
        int handle = ColorUtil.FromRgb(180, 83, 9);

        buf.FillRect(gx - 2, gy - 2, 5, 5, Outline);
        buf.FillRect(gx - 1, gy - 1, 3, 3, rim);
        buf.BlendRect(gx - 1, gy - 1, 3, 3, lens, 160);
        buf.SetPixel(gx, gy, ColorUtil.FromRgb(224, 242, 254));
        buf.FillRect(gx - 1, gy + 3, 2, 3, handle);
    }

    private static void DrawThoughtVariantProp(PixelBuffer buf, int x, int headTop, float t, int behaviorVariant)
    {
        if (behaviorVariant == 1)
        {
            // Variant 1: Pulsing golden idea lightbulb above head
            int by = headTop - 8;
            bool bright = ((int)(t * 4f) & 1) == 0;
            int bulb = bright ? ColorUtil.FromRgb(253, 224, 71) : ColorUtil.FromRgb(245, 158, 11);
            int glow = ColorUtil.FromRgb(254, 249, 195);
            buf.BlendRect(x - 3, by - 1, 6, 6, bulb, bright ? 80 : 40);
            buf.FillRect(x - 1, by, 3, 3, bulb);
            buf.SetPixel(x, by + 1, glow);
            buf.FillRect(x - 1, by + 3, 3, 1, ColorUtil.FromRgb(148, 163, 184));
            if (bright)
            {
                buf.SetPixel(x - 3, by + 1, glow);
                buf.SetPixel(x + 3, by + 1, glow);
                buf.SetPixel(x, by - 2, glow);
            }
            return;
        }

        if (behaviorVariant == 2)
        {
            // Variant 2: Mini animated loading bar above head
            int barY = headTop - 6;
            int fill = 1 + ((int)(t * 5f) % 6);
            buf.FillRect(x - 4, barY, 8, 3, Outline);
            buf.FillRect(x - 3, barY + 1, 6, 1, ColorUtil.FromRgb(30, 41, 59));
            buf.FillRect(x - 3, barY + 1, fill, 1, ColorUtil.FromRgb(56, 189, 248));
            return;
        }

        // Variant 0: Classic rising thought dots (...)
        int dotY = headTop - 5;
        int step = (int)(t * 4.5f) % 4;

        int cWhite = ColorUtil.FromRgb(248, 250, 252);
        int cSlate = ColorUtil.FromRgb(100, 116, 139);

        int d1 = step >= 1 ? cWhite : cSlate;
        int d2 = step >= 2 ? cWhite : cSlate;
        int d3 = step >= 3 ? cWhite : cSlate;

        buf.FillRect(x - 4, dotY + 1, 2, 2, d1);
        buf.FillRect(x - 1, dotY, 2, 2, d2);
        buf.FillRect(x + 3, dotY - 1, 2, 2, d3);
    }

    private static void DrawCommitPacket(PixelBuffer buf, int x, int torsoTop, float t)
    {
        int rise = (int)(t * 6f) % 6;
        int py = torsoTop - 3 - rise;
        int emerald = ColorUtil.FromRgb(34, 197, 94);
        int gold = ColorUtil.FromRgb(253, 224, 71);

        buf.FillRect(x - 2, py, 4, 3, emerald);
        buf.SetPixel(x - 1, py + 1, gold);
    }

    private static void DrawCodingSparks(PixelBuffer buf, int x, int torsoTop, float t, int behaviorVariant)
    {
        int spk = (int)(t * (10f + behaviorVariant * 2f)) % 4;
        int sx = (spk & 1) == 0 ? x - 6 : x + 5;
        int sy = torsoTop + 2 + (spk >> 1) * 3;
        int c1 = behaviorVariant switch
        {
            1 => ColorUtil.FromRgb(251, 146, 60),
            2 => ColorUtil.FromRgb(74, 222, 128),
            _ => ColorUtil.FromRgb(250, 204, 21)
        };
        int c2 = behaviorVariant switch
        {
            1 => ColorUtil.FromRgb(253, 224, 71),
            2 => ColorUtil.FromRgb(34, 211, 238),
            _ => ColorUtil.FromRgb(34, 211, 238)
        };
        buf.BlendPixel(sx, sy, c1, 220);
        buf.BlendPixel(sx + 1, sy - 1, c2, 180);
    }

    private static void DrawExecutionSparks(PixelBuffer buf, int x, int torsoTop, float t, int behaviorVariant)
    {
        int spk = (int)(t * (12f + behaviorVariant * 2f)) % 3;
        int sx = spk switch { 0 => x - 7, 1 => x, _ => x + 6 };
        int sy = torsoTop + 1 + (spk & 1) * 2;
        int c1 = behaviorVariant == 2 ? ColorUtil.FromRgb(250, 204, 21) : ColorUtil.FromRgb(34, 197, 94);
        int c2 = behaviorVariant == 1 ? ColorUtil.FromRgb(244, 114, 182) : ColorUtil.FromRgb(34, 211, 238);
        buf.BlendPixel(sx, sy, c1, 230);
        buf.BlendPixel(sx + 1, sy, c2, 200);
    }

    private static void DrawBackLayer(PixelBuffer buf, string key, int x, int headTop, int torsoTop, int footY, float t)
    {
        if (key == "shikamaru")
        {
            // Pineapple ponytail
            int hairDark = ColorUtil.FromRgb(24, 28, 38);
            int hairHi = ColorUtil.FromRgb(54, 62, 82);
            int sway = ((int)(t * 2f) & 1);
            buf.FillRect(x + 2, headTop - 5, 5, 5, Outline);
            buf.FillRect(x + 4 + sway, headTop - 6, 3, 3, Outline);
            buf.FillRect(x + 3, headTop - 4, 3, 4, hairDark);
            buf.SetPixel(x + 4 + sway, headTop - 5, hairDark);
            buf.SetPixel(x + 3, headTop - 3, hairHi);
        }
        else if (key == "tsunade")
        {
            // Twin blonde back pigtails
            int blonde = ColorUtil.FromRgb(250, 224, 96);
            int blondeShade = ColorUtil.FromRgb(218, 174, 48);
            buf.FillRect(x - 8, headTop + 4, 3, 9, Outline);
            buf.FillRect(x + 5, headTop + 4, 3, 9, Outline);
            buf.FillRect(x - 7, headTop + 5, 2, 7, blondeShade);
            buf.FillRect(x + 5, headTop + 5, 2, 7, blondeShade);
            buf.SetPixel(x - 7, headTop + 6, blonde);
            buf.SetPixel(x + 6, headTop + 6, blonde);
        }
        else if (key == "minato")
        {
            // White Hokage cloak
            int cloak = ColorUtil.FromRgb(241, 245, 249);
            int cloakShade = ColorUtil.FromRgb(203, 213, 225);
            int flameRed = ColorUtil.FromRgb(220, 38, 38);
            int flameBright = ColorUtil.FromRgb(239, 68, 68);
            int cloakH = Math.Min(11, Math.Max(7, footY - torsoTop - 1));
            buf.FillRect(x - 7, torsoTop, 14, cloakH + 1, Outline);
            buf.FillRect(x - 6, torsoTop + 1, 12, cloakH - 1, cloak);
            buf.FillRect(x - 6, torsoTop + 2, 2, cloakH - 3, cloakShade);
            buf.FillRect(x + 4, torsoTop + 2, 2, cloakH - 3, cloakShade);

            int hemY = torsoTop + cloakH - 2;
            buf.FillRect(x - 6, hemY, 12, 2, flameRed);
            for (int fx = x - 5; fx <= x + 4; fx += 3)
            {
                buf.SetPixel(fx, hemY - 1, flameBright);
                buf.SetPixel(fx + 1, hemY, flameBright);
            }
        }
        else if (key == "omp")
        {
            // Dark tech cloak shoulders
            int cloakDark = ColorUtil.FromRgb(15, 23, 42);
            int cyanTrim = ColorUtil.FromRgb(6, 182, 212);
            buf.FillRect(x - 7, torsoTop, 14, 8, Outline);
            buf.FillRect(x - 6, torsoTop + 1, 12, 7, cloakDark);
            buf.FillRect(x - 6, torsoTop + 6, 12, 1, cyanTrim);
        }
        else if (key == "agy")
        {
            // Sleek dark cosmic mantle with violet trim
            int mantleDark = ColorUtil.FromRgb(30, 27, 75);
            int violetTrim = ColorUtil.FromRgb(139, 92, 246);
            buf.FillRect(x - 7, torsoTop, 14, 8, Outline);
            buf.FillRect(x - 6, torsoTop + 1, 12, 7, mantleDark);
            buf.FillRect(x - 6, torsoTop + 6, 12, 1, violetTrim);
        }
        else if (key == "claude")
        {
            // Terracotta hood behind neck
            int hood = ColorUtil.FromRgb(154, 52, 18);
            buf.FillRect(x - 6, headTop + 2, 12, 6, Outline);
            buf.FillRect(x - 5, headTop + 3, 10, 5, hood);
        }
        else if (key == "codex")
        {
            // Emerald hood behind neck
            int hood = ColorUtil.FromRgb(6, 78, 59);
            buf.FillRect(x - 6, headTop + 2, 12, 6, Outline);
            buf.FillRect(x - 5, headTop + 3, 10, 5, hood);
        }
        else if (key == "mixi")
        {
            // Streamer hoodie collar behind neck with subtle crimson edge
            int hoodieDark = ColorUtil.FromRgb(18, 20, 28);
            int crimson = ColorUtil.FromRgb(220, 38, 38);
            buf.FillRect(x - 7, torsoTop, 14, 7, Outline);
            buf.FillRect(x - 6, torsoTop + 1, 12, 6, hoodieDark);
            buf.FillRect(x - 6, torsoTop + 1, 1, 4, crimson);
            buf.FillRect(x + 5, torsoTop + 1, 1, 4, crimson);
        }
        else if (key == "ronin-dev")
        {
            // Katana hilt peeking behind right shoulder + dark cloak hem
            int hilt = ColorUtil.FromRgb(245, 158, 11);
            int wrap = ColorUtil.FromRgb(185, 28, 28);
            int steel = ColorUtil.FromRgb(203, 213, 225);
            buf.FillRect(x + 4, headTop - 1, 3, 6, Outline);
            buf.SetPixel(x + 5, headTop, hilt);
            buf.SetPixel(x + 5, headTop + 1, wrap);
            buf.SetPixel(x + 5, headTop + 2, hilt);
            buf.SetPixel(x + 5, headTop + 3, steel);
        }
        else if (key == "astro-dev")
        {
            // Retro white-and-cyan oxygen backpack behind shoulders
            int pack = ColorUtil.FromRgb(203, 213, 225);
            int packShade = ColorUtil.FromRgb(148, 163, 184);
            int tankCyan = ColorUtil.FromRgb(34, 211, 238);
            buf.FillRect(x - 8, torsoTop, 16, 8, Outline);
            buf.FillRect(x - 7, torsoTop + 1, 2, 6, pack);
            buf.FillRect(x + 5, torsoTop + 1, 2, 6, packShade);
            buf.SetPixel(x - 7, torsoTop + 2, tankCyan);
            buf.SetPixel(x + 6, torsoTop + 2, tankCyan);
        }
        else if (key == "wizard-dev")
        {
            // Flowing arcane midnight cloak behind shoulders
            int cloak = ColorUtil.FromRgb(30, 27, 75);
            int violet = ColorUtil.FromRgb(124, 58, 237);
            int gold = ColorUtil.FromRgb(250, 204, 21);
            int cloakH = Math.Min(11, Math.Max(7, footY - torsoTop - 1));
            buf.FillRect(x - 7, torsoTop, 14, cloakH + 1, Outline);
            buf.FillRect(x - 6, torsoTop + 1, 12, cloakH - 1, cloak);
            buf.FillRect(x - 6, torsoTop + 2, 2, cloakH - 3, violet);
            buf.FillRect(x + 4, torsoTop + 2, 2, cloakH - 3, violet);
            buf.FillRect(x - 6, torsoTop + cloakH - 1, 12, 1, gold);
        }
        else if (key == "neko-dev")
        {
            // Cozy bob hair back strands
            int hair = ColorUtil.FromRgb(68, 44, 38);
            buf.FillRect(x - 7, headTop + 4, 2, 6, Outline);
            buf.FillRect(x + 5, headTop + 4, 2, 6, Outline);
            buf.FillRect(x - 6, headTop + 5, 1, 4, hair);
            buf.FillRect(x + 5, headTop + 5, 1, 4, hair);
        }
    }

    private static void DrawTorso(PixelBuffer buf, string key, int x, int torsoTop, int variantSeed)
    {
        buf.FillRect(x - 6, torsoTop - 1, 12, 10, Outline);

        switch (key)
        {
            case "shikamaru":
            case "kakashi":
                buf.FillRect(x - 5, torsoTop, 10, 8, ShinobiDark);
                buf.FillRect(x - 4, torsoTop, 8, 7, KonohaVest);
                buf.FillRect(x - 4, torsoTop + 5, 8, 2, KonohaVestShade);
                buf.FillRect(x - 3, torsoTop + 1, 2, 2, KonohaVestHi);
                buf.FillRect(x + 1, torsoTop + 1, 2, 2, KonohaVestHi);
                buf.FillRect(x, torsoTop, 1, 7, KonohaVestShade);
                break;

            case "minato":
                int navy = ColorUtil.FromRgb(30, 58, 138);
                int navyHi = ColorUtil.FromRgb(59, 130, 246);
                int cloak = ColorUtil.FromRgb(241, 245, 249);
                int flameRed = ColorUtil.FromRgb(220, 38, 38);
                buf.FillRect(x - 5, torsoTop, 10, 8, cloak);
                buf.FillRect(x - 2, torsoTop, 4, 7, navy);
                buf.SetPixel(x - 1, torsoTop + 1, navyHi);
                buf.FillRect(x - 5, torsoTop + 6, 10, 2, flameRed);
                buf.SetPixel(x - 4, torsoTop + 5, flameRed);
                buf.SetPixel(x + 3, torsoTop + 5, flameRed);
                break;

            case "tsunade":
                int haori = ColorUtil.FromRgb(77, 139, 91);
                int haoriShade = ColorUtil.FromRgb(52, 102, 64);
                int cream = ColorUtil.FromRgb(235, 232, 224);
                int sash = ColorUtil.FromRgb(56, 58, 72);
                buf.FillRect(x - 5, torsoTop, 10, 8, haori);
                buf.FillRect(x - 2, torsoTop, 4, 6, cream);
                buf.FillRect(x - 5, torsoTop + 5, 10, 2, sash);
                buf.FillRect(x - 5, torsoTop, 2, 8, haoriShade);
                buf.FillRect(x + 3, torsoTop, 2, 8, haoriShade);
                break;

            case "clone":
                int indigo = ColorUtil.FromRgb(49, 46, 129);
                int indigoDark = ColorUtil.FromRgb(30, 27, 75);
                int cyanTrim = ColorUtil.FromRgb(34, 211, 238);
                buf.FillRect(x - 5, torsoTop, 10, 8, indigo);
                buf.FillRect(x - 5, torsoTop + 5, 10, 3, indigoDark);
                buf.FillRect(x - 3, torsoTop + 1, 6, 1, cyanTrim);
                buf.FillRect(x, torsoTop + 1, 1, 6, cyanTrim);
                break;

            case "omp":
                int ompCloak = ColorUtil.FromRgb(24, 30, 42);
                int ompCyan = ColorUtil.FromRgb(6, 182, 212);
                int ompBright = ColorUtil.FromRgb(34, 211, 238);
                buf.FillRect(x - 5, torsoTop, 10, 8, ompCloak);
                buf.FillRect(x - 3, torsoTop + 2, 6, 2, ompCyan);
                buf.SetPixel(x - 1, torsoTop + 2, ompBright);
                buf.SetPixel(x, torsoTop + 2, ompBright);
                buf.FillRect(x - 4, torsoTop + 6, 8, 1, ompCyan);
                break;

            case "agy":
                int agyMantle = ColorUtil.FromRgb(30, 27, 75);
                int agyIndigo = ColorUtil.FromRgb(49, 46, 129);
                int agyViolet = ColorUtil.FromRgb(139, 92, 246);
                buf.FillRect(x - 5, torsoTop, 10, 8, agyMantle);
                buf.FillRect(x - 3, torsoTop + 1, 6, 6, agyIndigo);
                buf.FillRect(x - 1, torsoTop + 2, 2, 3, agyViolet);
                buf.SetPixel(x, torsoTop + 3, ColorUtil.FromRgb(192, 132, 252));
                break;

            case "claude":
                int claudeCoat = ColorUtil.FromRgb(194, 65, 12);
                int claudeAmber = ColorUtil.FromRgb(245, 158, 11);
                int claudeScarf = ColorUtil.FromRgb(254, 215, 170);
                buf.FillRect(x - 5, torsoTop, 10, 8, claudeCoat);
                buf.FillRect(x - 2, torsoTop, 4, 3, claudeScarf);
                buf.FillRect(x - 4, torsoTop + 5, 8, 2, claudeAmber);
                break;

            case "codex":
                int codexGreen = ColorUtil.FromRgb(16, 185, 129);
                int codexDark = ColorUtil.FromRgb(6, 78, 59);
                int codexMint = ColorUtil.FromRgb(110, 231, 183);
                buf.FillRect(x - 5, torsoTop, 10, 8, codexGreen);
                buf.FillRect(x - 5, torsoTop + 5, 10, 3, codexDark);
                buf.FillRect(x - 2, torsoTop + 2, 4, 2, codexMint);
                break;

            case "mixi":
                {
                    // Signature Độ Mixi Gaming black streamer tee/hoodie + silver chain + crimson chest emblem
                    int teeBlack = ColorUtil.FromRgb(20, 22, 30);
                    int teeShade = ColorUtil.FromRgb(12, 14, 20);
                    int crimson = ColorUtil.FromRgb(220, 38, 38);
                    int crimsonHi = ColorUtil.FromRgb(248, 113, 113);
                    int silver = ColorUtil.FromRgb(226, 232, 240);

                    buf.FillRect(x - 5, torsoTop, 10, 8, teeBlack);
                    buf.FillRect(x - 5, torsoTop + 6, 10, 2, teeShade);
                    // Silver chain at neckline
                    buf.SetPixel(x - 2, torsoTop + 1, silver);
                    buf.SetPixel(x - 1, torsoTop + 2, silver);
                    buf.SetPixel(x, torsoTop + 2, silver);
                    buf.SetPixel(x + 1, torsoTop + 1, silver);
                    // Signature crimson Mixi chest emblem
                    buf.FillRect(x - 2, torsoTop + 4, 4, 2, crimson);
                    buf.SetPixel(x - 1, torsoTop + 4, crimsonHi);
                    buf.SetPixel(x, torsoTop + 4, crimsonHi);
                    // Crimson shoulder piping
                    buf.FillRect(x - 5, torsoTop, 2, 1, crimson);
                    buf.FillRect(x + 3, torsoTop, 2, 1, crimson);
                    break;
                }

            case "neko-dev":
                {
                    // Pastel lavender lo-fi hoodie + pink heart/paw badge + white drawstrings
                    int hoodie = ColorUtil.FromRgb(167, 139, 250);
                    int hoodieShade = ColorUtil.FromRgb(124, 58, 237);
                    int pink = ColorUtil.FromRgb(244, 114, 182);
                    int creamStr = ColorUtil.FromRgb(254, 243, 199);

                    buf.FillRect(x - 5, torsoTop, 10, 8, hoodie);
                    buf.FillRect(x - 5, torsoTop + 5, 10, 3, hoodieShade);
                    buf.FillRect(x - 2, torsoTop + 1, 1, 3, creamStr);
                    buf.FillRect(x + 1, torsoTop + 1, 1, 3, creamStr);
                    buf.FillRect(x - 1, torsoTop + 4, 2, 2, pink);
                    break;
                }

            case "cyber-hacker":
                {
                    // Obsidian techwear jacket with neon cyan/magenta harness lines
                    int jacket = ColorUtil.FromRgb(18, 24, 38);
                    int panel = ColorUtil.FromRgb(30, 41, 59);
                    int cyan = ColorUtil.FromRgb(34, 211, 238);
                    int magenta = ColorUtil.FromRgb(236, 72, 153);

                    buf.FillRect(x - 5, torsoTop, 10, 8, jacket);
                    buf.FillRect(x - 3, torsoTop + 1, 6, 6, panel);
                    buf.FillRect(x - 4, torsoTop + 1, 1, 6, cyan);
                    buf.FillRect(x + 3, torsoTop + 1, 1, 6, magenta);
                    buf.FillRect(x - 2, torsoTop + 3, 4, 1, cyan);
                    buf.FillRect(x - 1, torsoTop + 5, 2, 1, magenta);
                    break;
                }

            case "ronin-dev":
                {
                    // Crimson & charcoal cyber-samurai haori + gold sash
                    int redHaori = ColorUtil.FromRgb(153, 27, 27);
                    int redHi = ColorUtil.FromRgb(220, 38, 38);
                    int darkUnder = ColorUtil.FromRgb(24, 30, 44);
                    int gold = ColorUtil.FromRgb(245, 158, 11);

                    buf.FillRect(x - 5, torsoTop, 10, 8, redHaori);
                    buf.FillRect(x - 2, torsoTop, 4, 6, darkUnder);
                    buf.FillRect(x - 5, torsoTop, 2, 3, redHi);
                    buf.FillRect(x + 3, torsoTop, 2, 3, redHi);
                    buf.FillRect(x - 5, torsoTop + 5, 10, 2, gold);
                    buf.SetPixel(x, torsoTop + 5, ColorUtil.FromRgb(34, 211, 238));
                    break;
                }

            case "astro-dev":
                {
                    // Retro white spacesuit with orange mission stripes & chest control unit
                    int suit = ColorUtil.FromRgb(241, 245, 249);
                    int suitShade = ColorUtil.FromRgb(203, 213, 225);
                    int orange = ColorUtil.FromRgb(249, 115, 22);
                    int cyanLed = ColorUtil.FromRgb(34, 211, 238);

                    buf.FillRect(x - 5, torsoTop, 10, 8, suit);
                    buf.FillRect(x - 5, torsoTop + 6, 10, 2, suitShade);
                    buf.FillRect(x - 5, torsoTop + 1, 10, 1, orange);
                    buf.FillRect(x - 2, torsoTop + 3, 4, 3, ColorUtil.FromRgb(51, 65, 85));
                    buf.SetPixel(x - 1, torsoTop + 4, cyanLed);
                    buf.SetPixel(x + 1, torsoTop + 4, orange);
                    break;
                }

            case "wizard-dev":
                {
                    // Starry midnight-indigo mage robe + golden rune sash
                    int robe = ColorUtil.FromRgb(49, 46, 129);
                    int robeDark = ColorUtil.FromRgb(30, 27, 75);
                    int gold = ColorUtil.FromRgb(250, 204, 21);
                    int cyanGem = ColorUtil.FromRgb(56, 189, 248);

                    buf.FillRect(x - 5, torsoTop, 10, 8, robe);
                    buf.FillRect(x - 5, torsoTop + 5, 10, 3, robeDark);
                    buf.FillRect(x - 1, torsoTop, 2, 8, gold);
                    buf.SetPixel(x, torsoTop + 2, cyanGem);
                    buf.SetPixel(x - 3, torsoTop + 4, gold);
                    buf.SetPixel(x + 3, torsoTop + 4, gold);
                    break;
                }

            case "mecha-droid":
                {
                    // Brushed silver-steel chassis + glowing cyan reactor core
                    int steel = ColorUtil.FromRgb(148, 163, 184);
                    int steelHi = ColorUtil.FromRgb(203, 213, 225);
                    int steelDark = ColorUtil.FromRgb(71, 85, 105);
                    int coreCyan = ColorUtil.FromRgb(34, 211, 238);
                    int coreGlow = ColorUtil.FromRgb(224, 242, 254);

                    buf.FillRect(x - 5, torsoTop, 10, 8, steel);
                    buf.FillRect(x - 4, torsoTop, 8, 1, steelHi);
                    buf.FillRect(x - 5, torsoTop + 5, 10, 3, steelDark);
                    buf.FillRect(x - 2, torsoTop + 2, 4, 3, Outline);
                    buf.FillRect(x - 1, torsoTop + 3, 2, 1, coreCyan);
                    buf.SetPixel(x, torsoTop + 3, coreGlow);
                    break;
                }

            case "street-gamer":
                {
                    // Emerald & gold varsity streetwear jacket + crisp white tee center
                    int green = ColorUtil.FromRgb(16, 185, 129);
                    int greenShade = ColorUtil.FromRgb(5, 150, 105);
                    int gold = ColorUtil.FromRgb(250, 204, 21);
                    int white = ColorUtil.FromRgb(248, 250, 252);

                    buf.FillRect(x - 5, torsoTop, 10, 8, green);
                    buf.FillRect(x - 5, torsoTop + 6, 10, 2, greenShade);
                    buf.FillRect(x - 2, torsoTop, 4, 6, white);
                    buf.FillRect(x - 5, torsoTop, 10, 1, gold);
                    buf.FillRect(x - 5, torsoTop + 6, 10, 1, gold);
                    buf.FillRect(x - 1, torsoTop + 2, 2, 2, ColorUtil.FromRgb(37, 99, 235));
                    break;
                }

            default:
                bool purple = (variantSeed & 1) == 1;
                int hoodieDef = purple ? ColorUtil.FromRgb(124, 58, 237) : ColorUtil.FromRgb(16, 185, 129);
                int hoodieDefShade = purple ? ColorUtil.FromRgb(91, 33, 182) : ColorUtil.FromRgb(5, 150, 105);
                int stringCol = ColorUtil.FromRgb(226, 232, 240);
                buf.FillRect(x - 5, torsoTop, 10, 8, hoodieDef);
                buf.FillRect(x - 5, torsoTop + 5, 10, 3, hoodieDefShade);
                buf.FillRect(x - 2, torsoTop + 1, 1, 3, stringCol);
                buf.FillRect(x + 1, torsoTop + 1, 1, 3, stringCol);
                buf.FillRect(x - 3, torsoTop + 5, 6, 2, hoodieDef);
                break;
        }
    }

    private static void DrawArms(
        PixelBuffer buf,
        string key,
        int x,
        int torsoTop,
        int headTop,
        bool armsUp,
        bool waveAlt,
        bool handsOnHead,
        bool typingFast,
        float t,
        AgentState state,
        int variantSeed,
        AgentActionKind actionKind = AgentActionKind.None)
    {
        int sleeveColor = ResolveSleeveColor(key, variantSeed);
        int handColor = ResolveHandColor(key);

        if (handsOnHead)
        {
            buf.FillRect(x - 8, headTop + 3, 3, 5, Outline);
            buf.FillRect(x + 5, headTop + 3, 3, 5, Outline);
            buf.FillRect(x - 7, headTop + 4, 2, 3, sleeveColor);
            buf.FillRect(x + 5, headTop + 4, 2, 3, sleeveColor);
            buf.FillRect(x - 6, headTop + 2, 2, 2, handColor);
            buf.FillRect(x + 4, headTop + 2, 2, 2, handColor);
            return;
        }

        if (armsUp)
        {
            int leftLift = state == AgentState.NeedsApproval && waveAlt ? 2 : 0;
            int rightLift = state == AgentState.NeedsApproval && !waveAlt ? 2 : 0;

            buf.FillRect(x - 8, torsoTop - 4 - leftLift, 3, 7, Outline);
            buf.FillRect(x - 7, torsoTop - 3 - leftLift, 2, 5, sleeveColor);
            buf.FillRect(x - 7, torsoTop - 5 - leftLift, 2, 2, handColor);

            buf.FillRect(x + 5, torsoTop - 4 - rightLift, 3, 7, Outline);
            buf.FillRect(x + 5, torsoTop - 3 - rightLift, 2, 5, sleeveColor);
            buf.FillRect(x + 5, torsoTop - 5 - rightLift, 2, 2, handColor);
            return;
        }

        // Action-specific arms
        if (state == AgentState.Working)
        {
            if (actionKind == AgentActionKind.Reading)
            {
                buf.FillRect(x - 7, torsoTop + 1, 3, 5, Outline);
                buf.FillRect(x - 6, torsoTop + 2, 2, 3, sleeveColor);
                buf.FillRect(x - 5, torsoTop + 3, 2, 2, handColor);

                buf.FillRect(x + 4, torsoTop + 1, 3, 5, Outline);
                buf.FillRect(x + 4, torsoTop + 2, 2, 3, sleeveColor);
                buf.FillRect(x + 3, torsoTop + 3, 2, 2, handColor);
                return;
            }

            if (actionKind == AgentActionKind.Thinking)
            {
                buf.FillRect(x - 7, torsoTop + 1, 2, 6, Outline);
                buf.FillRect(x - 6, torsoTop + 2, 1, 3, sleeveColor);
                buf.SetPixel(x - 6, torsoTop + 5, handColor);

                buf.FillRect(x + 4, torsoTop, 3, 6, Outline);
                buf.FillRect(x + 4, torsoTop + 1, 2, 4, sleeveColor);
                buf.FillRect(x + 2, headTop + 6, 2, 2, handColor);
                return;
            }

            if (actionKind == AgentActionKind.GitOps)
            {
                buf.FillRect(x - 6, torsoTop + 1, 3, 5, Outline);
                buf.FillRect(x + 3, torsoTop + 1, 3, 5, Outline);
                buf.FillRect(x - 5, torsoTop + 2, 2, 3, sleeveColor);
                buf.FillRect(x + 3, torsoTop + 2, 2, 3, sleeveColor);
                buf.FillRect(x - 1, torsoTop + 2, 3, 3, handColor);
                return;
            }

            if (actionKind == AgentActionKind.Searching)
            {
                buf.FillRect(x - 7, torsoTop + 1, 2, 6, Outline);
                buf.FillRect(x - 6, torsoTop + 2, 1, 3, sleeveColor);
                buf.SetPixel(x - 6, torsoTop + 5, handColor);

                buf.FillRect(x + 4, torsoTop + 1, 3, 5, Outline);
                buf.FillRect(x + 4, torsoTop + 2, 2, 3, sleeveColor);
                buf.FillRect(x + 3, torsoTop + 3, 2, 2, handColor);
                return;
            }

            if (actionKind == AgentActionKind.Reviewing)
            {
                buf.FillRect(x - 7, torsoTop + 1, 3, 5, Outline);
                buf.FillRect(x - 6, torsoTop + 2, 2, 3, sleeveColor);
                buf.FillRect(x - 5, torsoTop + 4, 2, 2, handColor);

                buf.FillRect(x + 4, torsoTop + 1, 2, 6, Outline);
                buf.FillRect(x + 4, torsoTop + 2, 1, 3, sleeveColor);
                buf.SetPixel(x + 4, torsoTop + 5, handColor);
                return;
            }
        }

        if (typingFast)
        {
            int tap = ((int)(t * 14f) & 1);
            int leftY = torsoTop + 3 + tap;
            int rightY = torsoTop + 3 + (1 - tap);

            buf.FillRect(x - 7, torsoTop + 1, 3, 5, Outline);
            buf.FillRect(x - 6, torsoTop + 2, 2, 3, sleeveColor);
            buf.FillRect(x - 5, leftY + 1, 2, 2, handColor);

            buf.FillRect(x + 4, torsoTop + 1, 3, 5, Outline);
            buf.FillRect(x + 4, torsoTop + 2, 2, 3, sleeveColor);
            buf.FillRect(x + 3, rightY + 1, 2, 2, handColor);
            return;
        }

        // Relaxed idle arms
        buf.FillRect(x - 7, torsoTop + 1, 2, 6, Outline);
        buf.FillRect(x - 6, torsoTop + 2, 1, 3, sleeveColor);
        buf.SetPixel(x - 6, torsoTop + 5, handColor);

        buf.FillRect(x + 5, torsoTop + 1, 2, 6, Outline);
        buf.FillRect(x + 5, torsoTop + 2, 1, 3, sleeveColor);
        buf.SetPixel(x + 5, torsoTop + 5, handColor);
    }

    private static void DrawHeadAndHair(
        PixelBuffer buf,
        string key,
        int x,
        int headTop,
        AgentState state,
        bool eyesBlink,
        bool eyesHappy,
        bool eyesPanic,
        float t,
        int variantSeed,
        int eyeScan = 0,
        AgentActionKind actionKind = AgentActionKind.None)
    {
        // Special full-head CRT face for Retro Pixel Bot (mecha-droid)
        if (key == "mecha-droid")
        {
            int metal = ColorUtil.FromRgb(148, 163, 184);
            int metalHi = ColorUtil.FromRgb(226, 232, 240);
            int metalShade = ColorUtil.FromRgb(71, 85, 105);
            int crtBg = ColorUtil.FromRgb(12, 22, 36);
            int cyan = ColorUtil.FromRgb(34, 211, 238);
            int emerald = ColorUtil.FromRgb(74, 222, 128);
            int red = ColorUtil.FromRgb(239, 68, 68);
            int amber = ColorUtil.FromRgb(253, 224, 71);

            // Top antenna with pulsing amber bulb
            buf.FillRect(x - 1, headTop - 5, 2, 4, Outline);
            buf.SetPixel(x, headTop - 4, metalHi);
            bool bulbOn = ((int)(t * 4f) & 1) == 0;
            buf.FillRect(x - 1, headTop - 6, 3, 2, bulbOn ? amber : ColorUtil.FromRgb(245, 158, 11));

            // Metallic CRT head housing
            buf.FillRect(x - 7, headTop - 1, 14, 10, Outline);
            buf.FillRect(x - 6, headTop, 12, 8, metal);
            buf.FillRect(x - 6, headTop, 12, 1, metalHi);
            buf.FillRect(x - 6, headTop + 7, 12, 1, metalShade);
            // Side ear bolts
            buf.FillRect(x - 8, headTop + 3, 2, 3, Outline);
            buf.SetPixel(x - 7, headTop + 4, cyan);
            buf.FillRect(x + 6, headTop + 3, 2, 3, Outline);
            buf.SetPixel(x + 6, headTop + 4, cyan);
            // CRT screen interior
            buf.FillRect(x - 5, headTop + 1, 10, 6, crtBg);

            int ey = headTop + 3;
            if (eyesPanic)
            {
                // X X error eyes
                buf.SetPixel(x - 4, ey, red); buf.SetPixel(x - 2, ey, red);
                buf.SetPixel(x - 3, ey + 1, red);
                buf.SetPixel(x - 4, ey + 2, red); buf.SetPixel(x - 2, ey + 2, red);
                buf.SetPixel(x + 1, ey, red); buf.SetPixel(x + 3, ey, red);
                buf.SetPixel(x + 2, ey + 1, red);
                buf.SetPixel(x + 1, ey + 2, red); buf.SetPixel(x + 3, ey + 2, red);
            }
            else if (eyesHappy)
            {
                // ^ ^ happy CRT eyes
                buf.SetPixel(x - 4, ey + 1, emerald); buf.SetPixel(x - 3, ey, emerald); buf.SetPixel(x - 2, ey + 1, emerald);
                buf.SetPixel(x + 1, ey + 1, emerald); buf.SetPixel(x + 2, ey, emerald); buf.SetPixel(x + 3, ey + 1, emerald);
            }
            else if (eyesBlink || state == AgentState.Idle)
            {
                // - - chill CRT eyes (or subtle pulsing dots)
                int idleCol = eyesBlink ? ColorUtil.FromRgb(56, 189, 248) : cyan;
                buf.FillRect(x - 4, ey + 1, 3, 1, idleCol);
                buf.FillRect(x + 1, ey + 1, 3, 1, idleCol);
                if (!eyesBlink)
                {
                    buf.FillRect(x - 1, ey + 3, 2, 1, ColorUtil.FromRgb(100, 116, 139));
                }
            }
            else
            {
                // Working > < focused CRT eyes
                buf.SetPixel(x - 4, ey, cyan); buf.SetPixel(x - 3, ey + 1, cyan); buf.SetPixel(x - 4, ey + 2, cyan);
                buf.SetPixel(x + 3, ey, cyan); buf.SetPixel(x + 2, ey + 1, cyan); buf.SetPixel(x + 3, ey + 2, cyan);
                buf.FillRect(x - 1, ey + 3, 2, 1, emerald);
            }
            return;
        }

        buf.FillRect(x - 6, headTop - 1, 12, 10, Outline);

        int faceColor = key == "clone" ? ColorUtil.FromRgb(186, 230, 253) : SkinBase;
        int faceShade = key == "clone" ? ColorUtil.FromRgb(125, 211, 252) : SkinShade;
        int faceHi = key == "clone" ? ColorUtil.FromRgb(224, 242, 254) : SkinHighlight;

        buf.FillRect(x - 5, headTop, 10, 8, faceColor);
        buf.FillRect(x - 5, headTop + 7, 10, 1, faceShade);
        buf.SetPixel(x - 4, headTop + 3, faceHi);

        int eyeY = headTop + 4;
        if (eyesHappy)
        {
            buf.SetPixel(x - 3, eyeY, EyeDark);
            buf.SetPixel(x - 2, eyeY - 1, EyeDark);
            buf.SetPixel(x - 1, eyeY, EyeDark);

            buf.SetPixel(x + 1, eyeY, EyeDark);
            buf.SetPixel(x + 2, eyeY - 1, EyeDark);
            buf.SetPixel(x + 3, eyeY, EyeDark);
        }
        else if (eyesBlink)
        {
            buf.FillRect(x - 3, eyeY + 1, 2, 1, EyeDark);
            buf.FillRect(x + 1, eyeY + 1, 2, 1, EyeDark);
        }
        else if (eyesPanic)
        {
            buf.FillRect(x - 4, eyeY - 1, 3, 3, EyeWhite);
            buf.FillRect(x + 1, eyeY - 1, 3, 3, EyeWhite);
            buf.SetPixel(x - 3, eyeY, EyeDark);
            buf.SetPixel(x + 2, eyeY, EyeDark);
            buf.FillRect(x - 1, headTop + 6, 2, 2, ColorUtil.FromRgb(153, 27, 27));
        }
        else
        {
            int lx = x - 3 + eyeScan;
            int rx = x + 1 + eyeScan;

            buf.FillRect(x - 3, eyeY, 2, 2, EyeDark);
            buf.SetPixel(Math.Clamp(lx, x - 4, x - 2), eyeY, EyeWhite);

            if (key == "kakashi" && (state == AgentState.Working || state == AgentState.Failed))
            {
                buf.FillRect(x + 1, eyeY, 2, 2, SharinganRed);
                buf.SetPixel(x + 1, eyeY, SharinganGlow);
                int pulse = 110 + (int)(MathF.Sin(t * 8f) * 50f);
                buf.BlendPixel(x + 3, eyeY, SharinganRed, pulse);
            }
            else
            {
                buf.FillRect(x + 1, eyeY, 2, 2, EyeDark);
                buf.SetPixel(Math.Clamp(rx, x + 1, x + 3), eyeY, EyeWhite);
            }

            if (actionKind == AgentActionKind.Reviewing)
            {
                buf.BlendPixel(x - 2, eyeY, ColorUtil.FromRgb(56, 189, 248), 200);
            }
        }

        switch (key)
        {
            case "shikamaru":
                {
                    int hair = ColorUtil.FromRgb(24, 28, 38);
                    int hairHi = ColorUtil.FromRgb(56, 64, 84);
                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, hair);
                    buf.FillRect(x - 3, headTop - 1, 4, 1, hairHi);
                    buf.FillRect(x - 5, headTop + 2, 1, 3, hair);
                    buf.FillRect(x + 4, headTop + 2, 1, 3, hair);
                    buf.FillRect(x - 5, headTop + 1, 10, 2, HeadbandCloth);
                    buf.FillRect(x - 2, headTop + 1, 4, 2, HeadbandMetal);
                    buf.SetPixel(x - 1, headTop + 1, EyeWhite);
                    break;
                }

            case "minato":
                {
                    int gold = ColorUtil.FromRgb(250, 204, 21);
                    int goldHi = ColorUtil.FromRgb(254, 240, 138);
                    int goldShade = ColorUtil.FromRgb(202, 138, 4);

                    buf.FillRect(x - 7, headTop - 2, 14, 4, Outline);
                    buf.FillRect(x - 5, headTop - 4, 3, 2, Outline);
                    buf.FillRect(x - 1, headTop - 5, 3, 3, Outline);
                    buf.FillRect(x + 3, headTop - 4, 3, 2, Outline);

                    buf.FillRect(x - 6, headTop - 1, 12, 3, gold);
                    buf.FillRect(x - 4, headTop - 3, 2, 2, gold);
                    buf.FillRect(x, headTop - 4, 2, 3, goldHi);
                    buf.FillRect(x + 3, headTop - 3, 2, 2, gold);
                    buf.FillRect(x - 5, headTop - 1, 8, 1, goldHi);

                    buf.FillRect(x - 6, headTop + 2, 2, 6, goldShade);
                    buf.FillRect(x + 4, headTop + 2, 2, 6, goldShade);
                    buf.SetPixel(x - 5, headTop + 2, gold);
                    buf.SetPixel(x + 4, headTop + 2, gold);

                    buf.FillRect(x - 4, headTop + 1, 8, 2, HeadbandCloth);
                    buf.FillRect(x - 2, headTop + 1, 4, 2, HeadbandMetal);
                    break;
                }

            case "tsunade":
                {
                    int blonde = ColorUtil.FromRgb(253, 224, 71);
                    int blondeHi = ColorUtil.FromRgb(254, 249, 195);
                    int blondeShade = ColorUtil.FromRgb(202, 138, 4);
                    int violet = ColorUtil.FromRgb(147, 51, 234);
                    int violetHi = ColorUtil.FromRgb(192, 132, 252);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, blonde);
                    buf.FillRect(x - 3, headTop - 1, 6, 1, blondeHi);
                    buf.FillRect(x - 6, headTop + 2, 2, 6, blonde);
                    buf.FillRect(x + 4, headTop + 2, 2, 6, blonde);
                    buf.SetPixel(x - 6, headTop + 6, blondeShade);
                    buf.SetPixel(x + 5, headTop + 6, blondeShade);

                    buf.SetPixel(x, headTop + 1, violet);
                    buf.SetPixel(x - 1, headTop + 2, violet);
                    buf.SetPixel(x, headTop + 2, violetHi);
                    buf.SetPixel(x + 1, headTop + 2, violet);
                    buf.SetPixel(x, headTop + 3, violet);
                    break;
                }

            case "kakashi":
                {
                    int silver = ColorUtil.FromRgb(226, 232, 240);
                    int silverHi = ColorUtil.FromRgb(255, 255, 255);
                    int silverShade = ColorUtil.FromRgb(148, 163, 184);
                    int mask = ColorUtil.FromRgb(24, 34, 52);
                    int maskHi = ColorUtil.FromRgb(44, 58, 84);

                    buf.FillRect(x - 7, headTop - 3, 13, 5, Outline);
                    buf.FillRect(x - 6, headTop - 5, 4, 2, Outline);
                    buf.FillRect(x - 2, headTop - 6, 5, 3, Outline);
                    buf.FillRect(x + 2, headTop - 4, 4, 2, Outline);

                    buf.FillRect(x - 6, headTop - 2, 11, 3, silver);
                    buf.FillRect(x - 5, headTop - 4, 3, 2, silverHi);
                    buf.FillRect(x - 1, headTop - 5, 3, 3, silverHi);
                    buf.FillRect(x + 2, headTop - 3, 3, 2, silver);
                    buf.FillRect(x - 5, headTop, 10, 1, silverShade);

                    buf.FillRect(x - 5, headTop + 1, 10, 2, HeadbandCloth);
                    buf.FillRect(x - 2, headTop + 1, 4, 2, HeadbandMetal);
                    if (state != AgentState.Working && state != AgentState.Failed)
                    {
                        buf.FillRect(x + 1, headTop + 3, 4, 2, HeadbandCloth);
                    }

                    buf.FillRect(x - 5, headTop + 6, 10, 3, mask);
                    buf.FillRect(x - 4, headTop + 6, 8, 1, maskHi);
                    break;
                }

            case "mixi":
                {
                    // SIGNATURE: Độ Mixi Gaming
                    // 1. Styled dark spiky/swept-up undercut hair (tóc vuốt dựng)
                    int hairDark = ColorUtil.FromRgb(20, 22, 32);
                    int hairMid = ColorUtil.FromRgb(42, 48, 66);
                    int hairHi = ColorUtil.FromRgb(82, 92, 118);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 4, headTop - 4, 3, 2, Outline);
                    buf.FillRect(x - 1, headTop - 5, 4, 3, Outline);
                    buf.FillRect(x + 2, headTop - 4, 3, 2, Outline);

                    buf.FillRect(x - 5, headTop - 1, 10, 3, hairDark);
                    buf.FillRect(x - 3, headTop - 3, 2, 2, hairDark);
                    buf.FillRect(x, headTop - 4, 3, 3, hairMid);
                    buf.FillRect(x + 2, headTop - 3, 2, 2, hairDark);
                    // Crisp swept-up styling highlights
                    buf.FillRect(x - 2, headTop - 2, 5, 1, hairHi);
                    buf.SetPixel(x + 1, headTop - 3, hairHi);
                    // Clean faded undercut sides (short above ears)
                    buf.SetPixel(x - 5, headTop + 2, hairMid);
                    buf.SetPixel(x + 4, headTop + 2, hairMid);

                    // 2. Bold black & crimson-red over-ear gaming headset
                    int hsBlack = ColorUtil.FromRgb(24, 26, 36);
                    int hsCrimson = ColorUtil.FromRgb(220, 38, 38);
                    int hsGlow = ColorUtil.FromRgb(254, 150, 150);

                    // Headset crown bridge arching cleanly over hair
                    buf.FillRect(x - 7, headTop - 3, 2, 6, Outline);
                    buf.FillRect(x + 5, headTop - 3, 2, 6, Outline);
                    buf.FillRect(x - 6, headTop - 3, 1, 5, hsBlack);
                    buf.FillRect(x + 5, headTop - 3, 1, 5, hsBlack);
                    buf.SetPixel(x - 6, headTop - 2, hsCrimson);
                    buf.SetPixel(x + 5, headTop - 2, hsCrimson);

                    // Glowing crimson over-ear gaming earcups
                    buf.FillRect(x - 8, headTop + 2, 3, 5, Outline);
                    buf.FillRect(x - 7, headTop + 3, 2, 3, hsBlack);
                    buf.FillRect(x - 7, headTop + 4, 1, 2, hsCrimson);
                    buf.SetPixel(x - 7, headTop + 3, hsGlow);

                    buf.FillRect(x + 5, headTop + 2, 3, 5, Outline);
                    buf.FillRect(x + 5, headTop + 3, 2, 3, hsBlack);
                    buf.FillRect(x + 6, headTop + 4, 1, 2, hsCrimson);
                    buf.SetPixel(x + 6, headTop + 3, hsGlow);

                    // Sleek boom mic extending from left earcup toward mouth
                    buf.SetPixel(x - 6, headTop + 6, hsBlack);
                    buf.SetPixel(x - 5, headTop + 7, hsBlack);
                    buf.SetPixel(x - 4, headTop + 7, hsBlack);
                    buf.FillRect(x - 3, headTop + 7, 2, 1, Outline);
                    buf.SetPixel(x - 2, headTop + 7, hsCrimson);

                    // 3. UNMISTAKABLE SIGNATURE FACIAL MOLE (mục ruồi) on right cheek!
                    // Keep surrounding cheek bright warm skin so the dark mole pops at 1x and 6x scale
                    buf.FillRect(x + 1, headTop + 6, 4, 2, SkinBase);
                    buf.SetPixel(x + 2, headTop + 6, SkinHighlight);
                    buf.SetPixel(x + 3, headTop + 6, ColorUtil.FromRgb(18, 12, 14)); // Iconic mole!
                    break;
                }

            case "neko-dev":
                {
                    // Cozy dark chestnut bob + pointed cat-ear headphones with pink inner ears
                    int hair = ColorUtil.FromRgb(68, 44, 38);
                    int hairHi = ColorUtil.FromRgb(112, 74, 62);
                    int earOuter = ColorUtil.FromRgb(46, 36, 68);
                    int earPink = ColorUtil.FromRgb(244, 114, 182);
                    int blush = ColorUtil.FromRgb(251, 146, 168);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, hair);
                    buf.FillRect(x - 4, headTop - 1, 7, 1, hairHi);
                    buf.FillRect(x - 5, headTop + 2, 1, 5, hair);
                    buf.FillRect(x + 4, headTop + 2, 1, 5, hair);

                    // Pointed cat-ear headphones
                    buf.FillRect(x - 6, headTop - 5, 4, 4, Outline);
                    buf.FillRect(x - 5, headTop - 4, 2, 3, earOuter);
                    buf.SetPixel(x - 5, headTop - 3, earPink);
                    buf.SetPixel(x - 4, headTop - 2, earPink);

                    buf.FillRect(x + 2, headTop - 5, 4, 4, Outline);
                    buf.FillRect(x + 3, headTop - 4, 2, 3, earOuter);
                    buf.SetPixel(x + 4, headTop - 3, earPink);
                    buf.SetPixel(x + 3, headTop - 2, earPink);

                    // Cute lo-fi cheek blush
                    buf.BlendPixel(x - 4, headTop + 6, blush, 170);
                    buf.BlendPixel(x + 3, headTop + 6, blush, 170);
                    break;
                }

            case "cyber-hacker":
                {
                    // Spiky electric-indigo hair + wide glowing neon cyan/magenta cyberpunk visor
                    int hair = ColorUtil.FromRgb(30, 27, 75);
                    int hairHi = ColorUtil.FromRgb(99, 102, 241);
                    int visorCyan = ColorUtil.FromRgb(34, 211, 238);
                    int visorMag = ColorUtil.FromRgb(236, 72, 153);
                    int visorHi = ColorUtil.FromRgb(224, 242, 254);

                    buf.FillRect(x - 6, headTop - 3, 12, 5, Outline);
                    buf.FillRect(x - 4, headTop - 5, 3, 2, Outline);
                    buf.FillRect(x + 1, headTop - 5, 3, 2, Outline);
                    buf.FillRect(x - 5, headTop - 2, 10, 4, hair);
                    buf.FillRect(x - 3, headTop - 4, 2, 2, hairHi);
                    buf.FillRect(x + 1, headTop - 4, 2, 2, hairHi);

                    // Wrap-around glowing neon visor
                    buf.FillRect(x - 6, headTop + 3, 12, 4, Outline);
                    buf.FillRect(x - 5, headTop + 4, 10, 2, visorCyan);
                    buf.FillRect(x + 1, headTop + 4, 4, 2, visorMag);
                    int glintX = x - 4 + ((int)(t * 5f) % 8);
                    buf.SetPixel(glintX, headTop + 4, visorHi);
                    break;
                }

            case "ronin-dev":
                {
                    // Iconic wide conical cyber-kasa hat + glowing cyan brim trim
                    int strawDark = ColorUtil.FromRgb(120, 82, 36);
                    int strawMid = ColorUtil.FromRgb(180, 128, 56);
                    int strawHi = ColorUtil.FromRgb(224, 176, 92);
                    int cyanRim = ColorUtil.FromRgb(34, 211, 238);

                    // Wide brim & stepped cone
                    buf.FillRect(x - 9, headTop, 18, 3, Outline);
                    buf.FillRect(x - 7, headTop - 2, 14, 3, Outline);
                    buf.FillRect(x - 4, headTop - 4, 8, 3, Outline);
                    buf.FillRect(x - 2, headTop - 5, 4, 2, Outline);

                    buf.FillRect(x - 8, headTop + 1, 16, 1, strawDark);
                    buf.FillRect(x - 6, headTop - 1, 12, 2, strawMid);
                    buf.FillRect(x - 3, headTop - 3, 6, 2, strawHi);
                    buf.FillRect(x - 1, headTop - 4, 2, 1, strawHi);
                    // Glowing cyan tech rim along front brim
                    buf.FillRect(x - 6, headTop + 2, 12, 1, cyanRim);
                    // Dark shadow mask over chin
                    buf.FillRect(x - 4, headTop + 6, 8, 2, ColorUtil.FromRgb(24, 30, 44));
                    break;
                }

            case "astro-dev":
                {
                    // Rounded glossy white space helmet + reflective golden visor + antenna
                    int shell = ColorUtil.FromRgb(241, 245, 249);
                    int shellShade = ColorUtil.FromRgb(148, 163, 184);
                    int goldVisor = ColorUtil.FromRgb(245, 158, 11);
                    int goldBright = ColorUtil.FromRgb(253, 224, 71);
                    int orangeTrim = ColorUtil.FromRgb(249, 115, 22);

                    // Tiny comms antenna on right
                    buf.FillRect(x + 5, headTop - 5, 2, 4, Outline);
                    buf.SetPixel(x + 5, headTop - 5, ColorUtil.FromRgb(34, 211, 238));

                    // Rounded helmet shell
                    buf.FillRect(x - 7, headTop - 2, 14, 11, Outline);
                    buf.FillRect(x - 6, headTop - 1, 12, 9, shell);
                    buf.FillRect(x - 5, headTop - 2, 10, 1, shell);
                    buf.FillRect(x - 6, headTop + 7, 12, 1, orangeTrim);
                    buf.FillRect(x - 6, headTop + 1, 1, 5, shellShade);
                    buf.FillRect(x + 5, headTop + 1, 1, 5, shellShade);

                    // Golden reflective faceplate visor
                    buf.FillRect(x - 5, headTop + 1, 10, 6, Outline);
                    buf.FillRect(x - 4, headTop + 2, 8, 4, goldVisor);
                    buf.FillRect(x - 3, headTop + 2, 4, 2, goldBright);
                    buf.SetPixel(x - 3, headTop + 2, EyeWhite);
                    buf.SetPixel(x - 2, headTop + 2, EyeWhite);
                    if (eyesPanic)
                    {
                        buf.FillRect(x - 2, headTop + 3, 4, 2, ColorUtil.FromRgb(239, 68, 68));
                    }
                    break;
                }

            case "wizard-dev":
                {
                    // Tall pointed starry wizard hat + golden band
                    int hatDark = ColorUtil.FromRgb(30, 27, 75);
                    int hatMid = ColorUtil.FromRgb(67, 56, 202);
                    int hatHi = ColorUtil.FromRgb(129, 140, 248);
                    int gold = ColorUtil.FromRgb(250, 204, 21);

                    // Wide brim
                    buf.FillRect(x - 8, headTop, 16, 3, Outline);
                    buf.FillRect(x - 7, headTop + 1, 14, 1, hatMid);
                    // Tall crooked pointed crown
                    buf.FillRect(x - 5, headTop - 2, 10, 3, Outline);
                    buf.FillRect(x - 3, headTop - 5, 7, 4, Outline);
                    buf.FillRect(x - 1, headTop - 7, 5, 3, Outline);
                    buf.FillRect(x - 4, headTop - 1, 8, 2, hatDark);
                    buf.FillRect(x - 2, headTop - 4, 5, 3, hatMid);
                    buf.FillRect(x, headTop - 6, 3, 2, hatHi);
                    // Golden hatband & star buckle
                    buf.FillRect(x - 5, headTop, 10, 1, gold);
                    buf.SetPixel(x, headTop - 1, gold);
                    buf.SetPixel(x, headTop - 3, ColorUtil.FromRgb(254, 249, 195));
                    break;
                }

            case "street-gamer":
                {
                    // Backwards royal-blue/crimson snapback cap + dark glossy shades
                    int capBlue = ColorUtil.FromRgb(37, 99, 235);
                    int capHi = ColorUtil.FromRgb(96, 165, 250);
                    int brimRed = ColorUtil.FromRgb(220, 38, 38);
                    int shades = ColorUtil.FromRgb(15, 23, 42);

                    buf.FillRect(x - 6, headTop - 3, 12, 5, Outline);
                    buf.FillRect(x - 5, headTop - 2, 10, 4, capBlue);
                    buf.FillRect(x - 4, headTop - 2, 6, 1, capHi);
                    // Backwards brim sticking out left
                    buf.FillRect(x - 9, headTop, 4, 3, Outline);
                    buf.FillRect(x - 8, headTop + 1, 3, 1, brimRed);
                    buf.FillRect(x - 5, headTop + 1, 10, 1, brimRed);

                    // Cool pixel shades with diagonal glint
                    if (!eyesHappy)
                    {
                        buf.FillRect(x - 5, headTop + 4, 10, 2, shades);
                        buf.SetPixel(x - 4, headTop + 4, EyeWhite);
                        buf.SetPixel(x + 1, headTop + 4, EyeWhite);
                    }
                    break;
                }

            case "clone":
                {
                    int hood = ColorUtil.FromRgb(30, 27, 75);
                    int cyan = ColorUtil.FromRgb(34, 211, 238);
                    int cyanHi = ColorUtil.FromRgb(165, 243, 252);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, hood);
                    buf.FillRect(x - 5, headTop + 1, 10, 2, cyan);
                    buf.FillRect(x - 2, headTop + 1, 4, 1, cyanHi);
                    buf.FillRect(x - 5, headTop + 6, 10, 2, hood);
                    break;
                }

            case "omp":
                {
                    int cyberHair = ColorUtil.FromRgb(30, 41, 59);
                    int cyberHairHi = ColorUtil.FromRgb(51, 65, 85);
                    int cyanVisor = ColorUtil.FromRgb(34, 211, 238);
                    int cyanGlow = ColorUtil.FromRgb(165, 243, 252);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, cyberHair);
                    buf.FillRect(x - 3, headTop - 2, 6, 1, cyberHairHi);
                    buf.FillRect(x - 5, headTop + 1, 10, 2, Outline);
                    buf.FillRect(x - 4, headTop + 1, 8, 2, cyanVisor);
                    buf.SetPixel(x - 2, headTop + 1, cyanGlow);
                    buf.SetPixel(x + 1, headTop + 1, cyanGlow);
                    break;
                }

            case "agy":
                {
                    int cosmicHair = ColorUtil.FromRgb(24, 18, 43);
                    int cosmicViolet = ColorUtil.FromRgb(139, 92, 246);
                    int starGlow = ColorUtil.FromRgb(192, 132, 252);
                    int starCyan = ColorUtil.FromRgb(56, 189, 248);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, cosmicHair);
                    buf.FillRect(x - 5, headTop + 2, 1, 4, cosmicHair);
                    buf.FillRect(x + 4, headTop + 2, 1, 4, cosmicHair);

                    buf.SetPixel(x, headTop + 1, starGlow);
                    buf.SetPixel(x - 1, headTop + 2, starCyan);
                    buf.SetPixel(x, headTop + 2, ColorUtil.FromRgb(248, 250, 252));
                    buf.SetPixel(x + 1, headTop + 2, starCyan);
                    buf.SetPixel(x, headTop + 3, cosmicViolet);
                    break;
                }

            case "claude":
                {
                    int hood = ColorUtil.FromRgb(194, 65, 12);
                    int hoodHi = ColorUtil.FromRgb(234, 88, 12);
                    int visor = ColorUtil.FromRgb(251, 191, 36);
                    int visorHi = ColorUtil.FromRgb(254, 240, 138);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 6, headTop + 1, 2, 7, Outline);
                    buf.FillRect(x + 4, headTop + 1, 2, 7, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, hood);
                    buf.FillRect(x - 4, headTop - 1, 8, 1, hoodHi);
                    buf.FillRect(x - 5, headTop + 2, 1, 6, hood);
                    buf.FillRect(x + 4, headTop + 2, 1, 6, hood);

                    if (!eyesBlink && !eyesHappy)
                    {
                        buf.FillRect(x - 4, headTop + 4, 3, 2, visor);
                        buf.FillRect(x + 1, headTop + 4, 3, 2, visor);
                        buf.SetPixel(x - 3, headTop + 4, visorHi);
                        buf.SetPixel(x + 2, headTop + 4, visorHi);
                    }
                    break;
                }

            case "codex":
                {
                    int hood = ColorUtil.FromRgb(6, 95, 70);
                    int hoodHi = ColorUtil.FromRgb(16, 185, 129);
                    int visor = ColorUtil.FromRgb(52, 211, 153);
                    int visorHi = ColorUtil.FromRgb(167, 243, 208);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 6, headTop + 1, 2, 7, Outline);
                    buf.FillRect(x + 4, headTop + 1, 2, 7, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, hood);
                    buf.FillRect(x - 4, headTop - 1, 8, 1, hoodHi);
                    buf.FillRect(x - 5, headTop + 2, 1, 6, hood);
                    buf.FillRect(x + 4, headTop + 2, 1, 6, hood);

                    if (!eyesBlink && !eyesHappy)
                    {
                        buf.FillRect(x - 4, headTop + 4, 3, 2, visor);
                        buf.FillRect(x + 1, headTop + 4, 3, 2, visor);
                        buf.SetPixel(x - 3, headTop + 4, visorHi);
                        buf.SetPixel(x + 2, headTop + 4, visorHi);
                    }
                    break;
                }

            default:
                {
                    bool purple = (variantSeed & 1) == 1;
                    int hood = purple ? ColorUtil.FromRgb(109, 40, 217) : ColorUtil.FromRgb(16, 185, 129);
                    int hoodHi = purple ? ColorUtil.FromRgb(167, 139, 250) : ColorUtil.FromRgb(110, 231, 183);
                    int visor = ColorUtil.FromRgb(56, 189, 248);

                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 6, headTop + 1, 2, 7, Outline);
                    buf.FillRect(x + 4, headTop + 1, 2, 7, Outline);

                    buf.FillRect(x - 5, headTop - 1, 10, 3, hood);
                    buf.FillRect(x - 4, headTop - 1, 8, 1, hoodHi);
                    buf.FillRect(x - 5, headTop + 2, 1, 6, hood);
                    buf.FillRect(x + 4, headTop + 2, 1, 6, hood);

                    if (!eyesBlink && !eyesHappy)
                    {
                        buf.BlendRect(x - 4, headTop + 4, 3, 2, visor, 150);
                        buf.BlendRect(x + 1, headTop + 4, 3, 2, visor, 150);
                    }
                    break;
                }
        }
    }

    public static void DrawTinyOrangeBook(PixelBuffer buf, int x, int y)
    {
        int cover = ColorUtil.FromRgb(249, 115, 22);
        int spine = ColorUtil.FromRgb(194, 65, 12);
        int pages = ColorUtil.FromRgb(255, 237, 213);
        buf.FillRect(x, y, 7, 6, Outline);
        buf.FillRect(x + 1, y + 1, 5, 4, cover);
        buf.FillRect(x + 3, y + 1, 1, 4, spine);
        buf.FillRect(x + 1, y + 1, 5, 1, pages);
    }

    public static void DrawCoffeeCup(PixelBuffer buf, int x, int y, float timeSeconds)
    {
        int mug = ColorUtil.FromRgb(226, 232, 240);
        int mugShade = ColorUtil.FromRgb(148, 163, 184);
        int coffee = ColorUtil.FromRgb(107, 62, 38);

        buf.FillRect(x, y, 6, 5, Outline);
        buf.FillRect(x + 1, y + 1, 4, 3, mug);
        buf.FillRect(x + 4, y + 2, 1, 2, mugShade);
        buf.FillRect(x + 1, y + 1, 4, 1, coffee);
        buf.SetPixel(x + 6, y + 2, mug);

        int step = (int)(timeSeconds * 4f) % 3;
        buf.BlendPixel(x + 2, y - 1 - step, EyeWhite, 160);
        buf.BlendPixel(x + 4, y - 2 - ((step + 1) % 3), EyeWhite, 125);
    }

    public static void DrawDesk(
        PixelBuffer buf,
        string stationType,
        int cx,
        int deskTopY,
        int deskTopColor,
        int deskFrontColor,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind = AgentActionKind.None,
        DeskRigKind deskRig = DeskRigKind.Default,
        int behaviorVariant = 0)
    {
        int deskW = 48;
        int x = cx - deskW / 2;

        // Subtle desk material tinting per rig so each computer setup has unmistakable identity
        int effectiveTop = deskRig switch
        {
            DeskRigKind.RgbStreamerRig => ColorUtil.FromRgb(32, 34, 46),
            DeskRigKind.CozyLofiRig => ColorUtil.FromRgb(146, 98, 58),
            DeskRigKind.RetroCrtRig => ColorUtil.FromRgb(176, 162, 134),
            DeskRigKind.CyberConsoleRig => ColorUtil.FromRgb(22, 28, 44),
            _ => deskTopColor
        };
        int effectiveFront = deskRig switch
        {
            DeskRigKind.RgbStreamerRig => ColorUtil.FromRgb(20, 22, 32),
            DeskRigKind.CozyLofiRig => ColorUtil.FromRgb(108, 68, 38),
            DeskRigKind.RetroCrtRig => ColorUtil.FromRgb(132, 120, 98),
            DeskRigKind.CyberConsoleRig => ColorUtil.FromRgb(14, 18, 30),
            _ => deskFrontColor
        };

        int topHighlight = ColorUtil.Lighten(effectiveTop, 0.16f);
        int frontShade = ColorUtil.Darken(effectiveFront, 0.25f);

        buf.BlendRect(x - 1, deskTopY + 18, deskW + 2, 3, ColorUtil.FromRgb(8, 10, 16), 115);

        buf.FillRect(x - 1, deskTopY - 1, deskW + 2, 20, Outline);
        buf.FillRect(x, deskTopY, deskW, 6, effectiveTop);
        buf.FillRect(x, deskTopY, deskW, 1, topHighlight);
        buf.FillRect(x, deskTopY + 6, deskW, 12, effectiveFront);
        buf.FillRect(x, deskTopY + 6, deskW, 1, frontShade);

        buf.FillRect(x + 4, deskTopY + 8, deskW - 8, 9, frontShade);
        buf.FillRect(x + 5, deskTopY + 9, deskW - 10, 7, ColorUtil.Darken(effectiveFront, 0.12f));

        if (state == AgentState.Failed)
        {
            DrawBurningLaptop(buf, cx - 10, deskTopY + 5, timeSeconds);
            return;
        }

        if (deskRig != DeskRigKind.Default)
        {
            switch (deskRig)
            {
                case DeskRigKind.DualMonitorRig:
                    DrawDualMonitorRigProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                    return;
                case DeskRigKind.RgbStreamerRig:
                    DrawRgbStreamerRigProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                    return;
                case DeskRigKind.CozyLofiRig:
                    DrawCozyLofiRigProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                    return;
                case DeskRigKind.RetroCrtRig:
                    DrawRetroCrtRigProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                    return;
                case DeskRigKind.CyberConsoleRig:
                    DrawCyberConsoleRigProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                    return;
            }
        }

        switch (stationType)
        {
            case "shikamaru":
                DrawScrollDeskProps(buf, cx, deskTopY, state, timeSeconds);
                break;
            case "minato":
                DrawDualMonitorProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                break;
            case "tsunade":
                DrawChakraLabProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                break;
            case "kakashi":
                DrawReviewDeskProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                break;
            default:
                DrawCloneTerminalProps(buf, cx, deskTopY, state, timeSeconds, actionKind, behaviorVariant);
                break;
        }
    }

    private static int GetCyclingRgbColor(float timeSeconds, int offset)
    {
        int idx = ((int)(timeSeconds * 4f) + offset) % 6;
        return idx switch
        {
            0 => ColorUtil.FromRgb(239, 68, 68),   // Crimson red
            1 => ColorUtil.FromRgb(249, 115, 22),  // Orange
            2 => ColorUtil.FromRgb(250, 204, 21),  // Gold
            3 => ColorUtil.FromRgb(34, 197, 94),   // Neon green
            4 => ColorUtil.FromRgb(34, 211, 238),  // Electric cyan
            _ => ColorUtil.FromRgb(192, 132, 252)  // Neon violet
        };
    }

    private static void DrawDualMonitorRigProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind,
        int behaviorVariant)
    {
        DrawMonitor(buf, cx - 22, deskTopY - 8, 15, 9, state, timeSeconds, 0, actionKind, behaviorVariant);
        DrawMonitor(buf, cx + 7, deskTopY - 8, 15, 9, state, timeSeconds, 2, actionKind, behaviorVariant);

        // Mechanical keyboard + mouse
        buf.FillRect(cx - 8, deskTopY + 2, 13, 3, ColorUtil.FromRgb(24, 32, 48));
        buf.FillRect(cx - 7, deskTopY + 3, 11, 1, ColorUtil.FromRgb(125, 211, 252));
        buf.FillRect(cx + 8, deskTopY + 2, 3, 2, ColorUtil.FromRgb(51, 65, 85));

        int strip = state == AgentState.Working ? ColorUtil.FromRgb(34, 211, 238) : ColorUtil.FromRgb(71, 85, 105);
        buf.FillRect(cx - 16, deskTopY + 12, 32, 1, strip);
    }

    private static void DrawRgbStreamerRigProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind,
        int behaviorVariant)
    {
        // Wide curved main streamer monitor (left-center)
        DrawMonitor(buf, cx - 18, deskTopY - 9, 20, 10, state, timeSeconds, 0, actionKind, behaviorVariant);
        // Subtle crimson streamer trim on top bezel
        buf.FillRect(cx - 16, deskTopY - 8, 16, 1, ColorUtil.FromRgb(220, 38, 38));

        // Left studio boom mic arm + pop filter + ring light glow
        int micArm = ColorUtil.FromRgb(30, 41, 59);
        buf.FillRect(cx - 22, deskTopY - 5, 2, 6, Outline);
        buf.FillRect(cx - 21, deskTopY - 4, 1, 5, micArm);
        buf.FillRect(cx - 20, deskTopY - 6, 3, 2, Outline);
        buf.SetPixel(cx - 19, deskTopY - 5, ColorUtil.FromRgb(239, 68, 68)); // Live mic LED

        // Right glowing RGB gaming PC tower with tempered glass & spinning RGB fans
        int pcX = cx + 10;
        int pcY = deskTopY - 10;
        buf.FillRect(pcX, pcY, 12, 13, Outline);
        buf.FillRect(pcX + 1, pcY + 1, 10, 11, ColorUtil.FromRgb(24, 26, 36));
        buf.FillRect(pcX + 2, pcY + 2, 8, 9, ColorUtil.FromRgb(12, 14, 22)); // Glass window
        int rgb1 = GetCyclingRgbColor(timeSeconds, 0);
        int rgb2 = GetCyclingRgbColor(timeSeconds, 2);
        int rgb3 = GetCyclingRgbColor(timeSeconds, 4);
        buf.FillRect(pcX + 3, pcY + 3, 3, 3, rgb1);
        buf.FillRect(pcX + 6, pcY + 5, 3, 3, rgb2);
        buf.FillRect(pcX + 3, pcY + 8, 6, 1, rgb3);
        buf.BlendRect(pcX - 1, pcY, 14, 13, rgb1, 32);

        // RGB backlit mechanical keyboard
        buf.FillRect(cx - 12, deskTopY + 2, 16, 3, Outline);
        buf.FillRect(cx - 11, deskTopY + 3, 5, 1, rgb1);
        buf.FillRect(cx - 6, deskTopY + 3, 5, 1, rgb2);
        buf.FillRect(cx - 1, deskTopY + 3, 4, 1, rgb3);

        // Cycling RGB under-desk neon light bar
        for (int seg = 0; seg < 6; seg++)
        {
            int segCol = GetCyclingRgbColor(timeSeconds, seg);
            buf.FillRect(cx - 18 + seg * 6, deskTopY + 11, 6, 2, segCol);
        }
        buf.BlendRect(cx - 19, deskTopY + 13, 38, 3, rgb2, 42);
    }

    private static void DrawCozyLofiRigProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind,
        int behaviorVariant)
    {
        // Sleek silver-aluminum laptop centered on desk
        int lapX = cx - 9;
        int lapY = deskTopY - 6;
        DrawMonitor(buf, lapX, lapY, 16, 8, state, timeSeconds, 1, actionKind, behaviorVariant);
        // Silver laptop base deck
        buf.FillRect(lapX - 1, deskTopY + 1, 18, 3, Outline);
        buf.FillRect(lapX, deskTopY + 2, 16, 1, ColorUtil.FromRgb(203, 213, 225));

        // Tiny potted succulent plant on left
        int px = cx - 21;
        int py = deskTopY - 5;
        buf.FillRect(px + 1, py + 2, 6, 5, Outline);
        buf.FillRect(px + 2, py + 3, 4, 3, ColorUtil.FromRgb(194, 102, 54)); // Terracotta pot
        buf.FillRect(px + 1, py, 2, 3, ColorUtil.FromRgb(34, 197, 94));
        buf.FillRect(px + 3, py - 1, 2, 4, ColorUtil.FromRgb(74, 222, 128));
        buf.FillRect(px + 5, py + 1, 2, 2, ColorUtil.FromRgb(22, 163, 74));

        // Warm amber lamp glow + steaming coffee mug on right
        int warmAmber = ColorUtil.FromRgb(251, 191, 36);
        buf.DrawCircle(cx + 15, deskTopY - 4, 7, ColorUtil.WithAlpha(warmAmber, 38), true);
        DrawCoffeeCup(buf, cx + 11, deskTopY - 3, timeSeconds);

        // Cozy wooden drawer handle accent
        buf.FillRect(cx - 4, deskTopY + 11, 8, 2, ColorUtil.FromRgb(217, 152, 82));
    }

    private static void DrawRetroCrtRigProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind,
        int behaviorVariant)
    {
        // Chunky 90s beige/cream CRT monitor
        int crtX = cx - 18;
        int crtY = deskTopY - 10;
        int crtW = 19;
        int crtH = 11;
        int beige = ColorUtil.FromRgb(224, 214, 188);
        int beigeShade = ColorUtil.FromRgb(176, 164, 138);
        int crtScreen = ColorUtil.FromRgb(10, 32, 20);
        int phosphor = state == AgentState.NeedsApproval
            ? ColorUtil.FromRgb(251, 191, 36)
            : ColorUtil.FromRgb(74, 222, 128);

        // Swivel base
        buf.FillRect(crtX + 4, crtY + crtH, 11, 2, Outline);
        buf.FillRect(crtX + 5, crtY + crtH, 9, 1, beigeShade);

        // Chunky CRT housing
        buf.FillRect(crtX, crtY, crtW, crtH, Outline);
        buf.FillRect(crtX + 1, crtY + 1, crtW - 2, crtH - 2, beige);
        buf.FillRect(crtX + crtW - 3, crtY + 1, 2, crtH - 2, beigeShade);
        buf.FillRect(crtX + 2, crtY + 2, crtW - 6, crtH - 4, Outline);
        buf.FillRect(crtX + 3, crtY + 3, crtW - 8, crtH - 6, crtScreen);

        // Phosphor green scanlines / blinking cursor
        if (state == AgentState.Working)
        {
            int shift = (int)(timeSeconds * 6f);
            for (int row = 0; row < crtH - 7; row += 2)
            {
                int len = 3 + ((shift + row * 3) % Math.Max(2, crtW - 11));
                buf.FillRect(crtX + 4, crtY + 4 + row, len, 1, phosphor);
            }
            buf.BlendRect(crtX + 2, crtY + crtH, crtW - 4, 2, phosphor, 32);
        }
        else if (state == AgentState.Done)
        {
            buf.FillRect(crtX + 4, crtY + 5, crtW - 10, 2, phosphor);
        }
        else if (((int)(timeSeconds * 2f) & 1) == 0)
        {
            buf.FillRect(crtX + 4, crtY + 4, 2, 2, phosphor);
        }

        // Tiny green power LED on CRT bezel
        buf.SetPixel(crtX + crtW - 3, crtY + crtH - 3, ColorUtil.FromRgb(34, 197, 94));

        // Chunky beige mechanical keyboard
        buf.FillRect(cx - 12, deskTopY + 2, 16, 3, Outline);
        buf.FillRect(cx - 11, deskTopY + 3, 14, 1, beige);

        // Stack of colorful 3.5" floppy disks on right
        int fx = cx + 10;
        int fy = deskTopY - 4;
        buf.FillRect(fx, fy, 9, 6, Outline);
        buf.FillRect(fx + 1, fy + 1, 7, 4, ColorUtil.FromRgb(37, 99, 235)); // Blue floppy
        buf.FillRect(fx + 3, fy + 1, 3, 2, ColorUtil.FromRgb(226, 232, 240)); // Metal shutter
        buf.FillRect(fx + 2, fy + 4, 5, 1, ColorUtil.FromRgb(250, 204, 21)); // Label
        buf.FillRect(fx + 1, fy - 2, 7, 2, ColorUtil.FromRgb(220, 38, 38)); // Top red floppy edge
    }

    private static void DrawCyberConsoleRigProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind,
        int behaviorVariant)
    {
        int cyan = ColorUtil.FromRgb(34, 211, 238);
        int magenta = ColorUtil.FromRgb(236, 72, 153);

        // Wide angular obsidian cyber HUD monitor
        DrawMonitor(buf, cx - 19, deskTopY - 8, 22, 9, state, timeSeconds, 0, actionKind, behaviorVariant);

        // Floating translucent holographic HUD ticker hovering above monitor
        int hover = ((int)(timeSeconds * 3f) & 1);
        int holoY = deskTopY - 12 - hover;
        buf.BlendRect(cx - 16, holoY, 16, 3, cyan, 120);
        buf.FillRect(cx - 14, holoY + 1, 5, 1, ColorUtil.FromRgb(224, 242, 254));
        buf.FillRect(cx - 7, holoY + 1, 5, 1, magenta);

        // Right holographic data core pylon
        buf.FillRect(cx + 10, deskTopY - 7, 8, 9, Outline);
        buf.FillRect(cx + 11, deskTopY - 6, 6, 7, ColorUtil.FromRgb(15, 23, 42));
        int pulse = ((int)(timeSeconds * 5f) & 1) == 0 ? cyan : magenta;
        buf.FillRect(cx + 12, deskTopY - 5, 4, 2, pulse);
        buf.FillRect(cx + 12, deskTopY - 2, 4, 2, cyan);

        // Angular illuminated console keys
        buf.FillRect(cx - 12, deskTopY + 2, 18, 3, Outline);
        buf.FillRect(cx - 11, deskTopY + 3, 10, 1, cyan);
        buf.FillRect(cx, deskTopY + 3, 5, 1, magenta);

        // Dual magenta/cyan front neon accent strips
        buf.FillRect(cx - 18, deskTopY + 10, 16, 2, cyan);
        buf.FillRect(cx + 2, deskTopY + 10, 16, 2, magenta);
    }

    private static void DrawScrollDeskProps(PixelBuffer buf, int cx, int deskTopY, AgentState state, float timeSeconds)
    {
        int scrollW = 24;
        int sx = cx - scrollW / 2 - 2;
        int sy = deskTopY - 2;
        int parchment = ColorUtil.FromRgb(245, 232, 198);
        int roller = ColorUtil.FromRgb(120, 53, 15);
        int runeColor = state == AgentState.Working
            ? ColorUtil.FromRgb(14, 165, 233)
            : (state == AgentState.NeedsApproval ? ColorUtil.FromRgb(245, 158, 11) : ColorUtil.FromRgb(100, 116, 139));

        buf.FillRect(sx - 2, sy - 1, scrollW + 4, 8, Outline);
        buf.FillRect(sx - 1, sy, 2, 6, roller);
        buf.FillRect(sx + scrollW - 1, sy, 2, 6, roller);
        buf.FillRect(sx + 1, sy, scrollW - 2, 6, parchment);

        int anim = (int)(timeSeconds * 4f) % 4;
        buf.FillRect(sx + 3, sy + 1, 7 + (state == AgentState.Working ? anim : 0), 1, runeColor);
        buf.FillRect(sx + 3, sy + 3, 14, 1, runeColor);
        buf.FillRect(sx + 3, sy + 4, 9, 1, ColorUtil.Darken(runeColor, 0.3f));

        DrawCoffeeCup(buf, cx + 14, deskTopY - 3, timeSeconds);
    }

    private static void DrawDualMonitorProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind = AgentActionKind.None,
        int behaviorVariant = 0)
    {
        DrawMonitor(buf, cx - 22, deskTopY - 8, 15, 9, state, timeSeconds, 0, actionKind, behaviorVariant);
        DrawMonitor(buf, cx + 7, deskTopY - 8, 15, 9, state, timeSeconds, 2, actionKind, behaviorVariant);

        buf.FillRect(cx - 7, deskTopY + 2, 14, 3, ColorUtil.FromRgb(30, 41, 59));
        buf.FillRect(cx - 6, deskTopY + 3, 12, 1, ColorUtil.FromRgb(100, 116, 139));
    }

    private static void DrawChakraLabProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind = AgentActionKind.None,
        int behaviorVariant = 0)
    {
        DrawMonitor(buf, cx - 22, deskTopY - 8, 15, 9, state, timeSeconds, 1, actionKind, behaviorVariant);

        int fx = cx + 8;
        int fy = deskTopY - 7;
        int glass = ColorUtil.FromRgb(203, 213, 225);
        int chakra = ColorUtil.FromRgb(34, 197, 94);
        int chakraHi = ColorUtil.FromRgb(134, 239, 172);

        buf.FillRect(fx + 1, fy, 4, 4, Outline);
        buf.FillRect(fx - 1, fy + 3, 8, 6, Outline);
        buf.FillRect(fx + 2, fy + 1, 2, 3, glass);
        buf.FillRect(fx, fy + 4, 6, 4, chakra);
        buf.FillRect(fx + 1, fy + 4, 3, 1, chakraHi);

        int vx = cx + 16;
        buf.FillRect(vx, fy + 2, 5, 7, Outline);
        buf.FillRect(vx + 1, fy + 3, 3, 2, glass);
        buf.FillRect(vx + 1, fy + 5, 3, 3, chakra);

        if (state == AgentState.Working)
        {
            int b1 = (int)(timeSeconds * 6f) % 4;
            int b2 = (int)(timeSeconds * 5f + 2f) % 4;
            buf.BlendPixel(fx + 2, fy - 1 - b1, chakraHi, 220);
            buf.BlendPixel(vx + 2, fy + 1 - b2, chakra, 210);
            buf.BlendRect(fx - 2, fy + 3, 10, 6, chakra, 45);
        }
    }

    private static void DrawReviewDeskProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind = AgentActionKind.None,
        int behaviorVariant = 0)
    {
        DrawMonitor(buf, cx - 22, deskTopY - 8, 16, 9, state, timeSeconds, 3, actionKind, behaviorVariant);

        int parchment = ColorUtil.FromRgb(236, 224, 196);
        int tie = ColorUtil.FromRgb(220, 38, 38);
        buf.FillRect(cx + 13, deskTopY - 3, 8, 5, Outline);
        buf.FillRect(cx + 14, deskTopY - 2, 6, 3, parchment);
        buf.FillRect(cx + 17, deskTopY - 2, 1, 3, tie);

        if (state != AgentState.Idle)
        {
            DrawTinyOrangeBook(buf, cx + 6, deskTopY - 2);
        }
    }

    private static void DrawCloneTerminalProps(
        PixelBuffer buf,
        int cx,
        int deskTopY,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind = AgentActionKind.None,
        int behaviorVariant = 0)
    {
        DrawMonitor(buf, cx - 21, deskTopY - 7, 15, 8, state, timeSeconds, 0, actionKind, behaviorVariant);
        buf.FillRect(cx - 4, deskTopY + 2, 14, 3, ColorUtil.FromRgb(30, 41, 59));
        buf.FillRect(cx - 3, deskTopY + 3, 12, 1, ColorUtil.FromRgb(56, 189, 248));

        int stripColor = state == AgentState.Working
            ? ColorUtil.FromRgb(34, 211, 238)
            : ColorUtil.FromRgb(71, 85, 105);
        buf.FillRect(cx - 14, deskTopY + 11, 28, 2, stripColor);
    }

    public static void DrawMonitor(
        PixelBuffer buf,
        int x,
        int y,
        int w,
        int h,
        AgentState state,
        float timeSeconds,
        int screenMode,
        AgentActionKind actionKind = AgentActionKind.None,
        int behaviorVariant = 0)
    {
        int bezel = ColorUtil.FromRgb(30, 41, 59);
        int bezelHi = ColorUtil.FromRgb(71, 85, 105);
        int screenBg = state == AgentState.Idle
            ? ColorUtil.FromRgb(15, 23, 42)
            : ColorUtil.FromRgb(10, 25, 41);

        // Stand
        buf.FillRect(x + w / 2 - 2, y + h, 4, 3, Outline);
        buf.FillRect(x + w / 2 - 4, y + h + 2, 8, 2, bezel);

        // Frame
        buf.FillRect(x, y, w, h, Outline);
        buf.FillRect(x + 1, y + 1, w - 2, h - 2, bezel);
        buf.FillRect(x + 1, y + 1, w - 2, 1, bezelHi);
        buf.FillRect(x + 2, y + 2, w - 4, h - 4, screenBg);

        int innerW = w - 6;
        if (innerW <= 2) return;
        int variant = Math.Abs(behaviorVariant) % 3;

        if (state == AgentState.Working)
        {
            switch (actionKind)
            {
                case AgentActionKind.Executing:
                    {
                        // Terminal prompt >_
                        buf.SetPixel(x + 3, y + 3, ColorUtil.FromRgb(34, 197, 94));
                        buf.SetPixel(x + 4, y + 4, ColorUtil.FromRgb(34, 197, 94));
                        buf.SetPixel(x + 3, y + 5, ColorUtil.FromRgb(34, 197, 94));
                        if (((int)(timeSeconds * 4f) & 1) == 0)
                        {
                            buf.FillRect(x + 6, y + 5, 3, 1, ColorUtil.FromRgb(253, 224, 71));
                        }
                        buf.BlendRect(x + 1, y + h, w - 2, 3, ColorUtil.FromRgb(34, 197, 94), 35);
                        break;
                    }

                case AgentActionKind.Reading:
                    {
                        // Document lines with moving scan highlight
                        int scanLine = ((int)(timeSeconds * 3f) % Math.Max(1, (h - 5)));
                        for (int row = 0; row < h - 5; row += 2)
                        {
                            int col = row == scanLine ? ColorUtil.FromRgb(56, 189, 248) : ColorUtil.FromRgb(100, 116, 139);
                            buf.FillRect(x + 3, y + 3 + row, innerW, 1, col);
                        }
                        buf.BlendRect(x + 1, y + h, w - 2, 3, ColorUtil.FromRgb(56, 189, 248), 30);
                        break;
                    }

                case AgentActionKind.Searching:
                    {
                        // Radar sweep beam
                        int sweepCol = ((int)(timeSeconds * 9f) % innerW);
                        buf.FillRect(x + 3 + sweepCol, y + 3, 2, Math.Max(2, h - 6), ColorUtil.FromRgb(34, 211, 238));
                        buf.BlendRect(x + 3, y + 3, innerW, Math.Max(2, h - 6), ColorUtil.FromRgb(14, 52, 74), 70);
                        buf.BlendRect(x + 1, y + h, w - 2, 3, ColorUtil.FromRgb(34, 211, 238), 35);
                        break;
                    }

                case AgentActionKind.Testing:
                case AgentActionKind.Building:
                    {
                        // Animated progress bar
                        int progW = Math.Max(2, ((int)(timeSeconds * 5f) % (innerW + 1)));
                        int barCol = actionKind == AgentActionKind.Testing ? ColorUtil.FromRgb(34, 197, 94) : ColorUtil.FromRgb(245, 158, 11);
                        buf.FillRect(x + 3, y + h / 2 - 1, progW, 2, barCol);
                        buf.FillRect(x + 3, y + 3, 4, 1, barCol);
                        buf.BlendRect(x + 1, y + h, w - 2, 3, barCol, 35);
                        break;
                    }

                case AgentActionKind.GitOps:
                    {
                        // Commit nodes and tree
                        buf.FillRect(x + 3, y + h / 2, innerW, 1, ColorUtil.FromRgb(34, 197, 94));
                        buf.FillRect(x + 4, y + h / 2 - 1, 3, 3, ColorUtil.FromRgb(253, 224, 71));
                        buf.FillRect(x + 9, y + h / 2 - 1, 3, 3, ColorUtil.FromRgb(34, 197, 94));
                        buf.BlendRect(x + 1, y + h, w - 2, 3, ColorUtil.FromRgb(34, 197, 94), 30);
                        break;
                    }

                case AgentActionKind.Reviewing:
                    {
                        // Diff lines: green +, crimson -
                        buf.FillRect(x + 3, y + 3, 2, 1, ColorUtil.FromRgb(34, 197, 94));
                        buf.FillRect(x + 6, y + 3, innerW - 4, 1, ColorUtil.FromRgb(74, 222, 128));
                        buf.FillRect(x + 3, y + 5, 2, 1, ColorUtil.FromRgb(239, 68, 68));
                        buf.FillRect(x + 6, y + 5, innerW - 4, 1, ColorUtil.FromRgb(248, 113, 113));
                        buf.BlendRect(x + 1, y + h, w - 2, 3, ColorUtil.FromRgb(56, 189, 248), 30);
                        break;
                    }

                case AgentActionKind.Thinking:
                    {
                        // Pulsing waveform
                        int waveShift = (int)(timeSeconds * 4f);
                        for (int wx = 0; wx < innerW; wx++)
                        {
                            int wy = (int)(MathF.Sin((wx + waveShift) * 0.9f) * 1.5f);
                            buf.SetPixel(x + 3 + wx, y + h / 2 + wy, ColorUtil.FromRgb(192, 132, 252));
                        }
                        buf.BlendRect(x + 1, y + h, w - 2, 3, ColorUtil.FromRgb(192, 132, 252), 30);
                        break;
                    }

                default: // Coding or None
                    {
                        int scroll = (int)(timeSeconds * 8f);
                        int c1 = variant == 2
                            ? ColorUtil.FromRgb(34, 197, 94)
                            : screenMode switch
                            {
                                1 => ColorUtil.FromRgb(74, 222, 128),
                                3 => ColorUtil.FromRgb(248, 113, 113),
                                _ => ColorUtil.FromRgb(56, 189, 248)
                            };
                        int c2 = variant == 2
                            ? ColorUtil.FromRgb(134, 239, 172)
                            : screenMode switch
                            {
                                1 => ColorUtil.FromRgb(134, 239, 172),
                                3 => ColorUtil.FromRgb(74, 222, 128),
                                _ => ColorUtil.FromRgb(250, 204, 21)
                            };

                        for (int row = 0; row < h - 5; row += 2)
                        {
                            int lineLen = 3 + ((scroll + row * 3 + screenMode * 5) % (innerW - 2));
                            int col = ((row + scroll) & 1) == 0 ? c1 : c2;
                            buf.FillRect(x + 3, y + 3 + row, lineLen, 1, col);
                        }
                        buf.BlendRect(x + 1, y + h, w - 2, 3, c1, 35);
                        break;
                    }
            }
        }
        else if (state == AgentState.Done)
        {
            buf.FillRect(x + 3, y + 4, innerW, 2, ColorUtil.FromRgb(34, 197, 94));
        }
        else if (state == AgentState.NeedsApproval)
        {
            int pulse = ((int)(timeSeconds * 4f) & 1) == 0
                ? ColorUtil.FromRgb(245, 158, 11)
                : ColorUtil.FromRgb(253, 224, 71);
            buf.FillRect(x + 3, y + 4, innerW, 2, pulse);
        }
        else
        {
            // Subtle Idle screen behavior variants
            if (variant == 1)
            {
                // Bouncing pixel screensaver dot
                int spanX = Math.Max(1, innerW - 2);
                int px = (int)(timeSeconds * 4f) % (spanX * 2);
                if (px >= spanX) px = spanX * 2 - 1 - px;
                int py = ((int)(timeSeconds * 2f) & 1);
                buf.FillRect(x + 3 + px, y + 3 + py, 2, 2, ColorUtil.FromRgb(56, 189, 248));
            }
            else if (variant == 2)
            {
                // Gentle blinking standby LED in corner
                if (((int)(timeSeconds * 1.5f) & 1) == 0)
                {
                    buf.SetPixel(x + w - 4, y + h - 4, ColorUtil.FromRgb(251, 191, 36));
                }
            }
            else if (((int)(timeSeconds * 2f) & 1) == 0)
            {
                buf.FillRect(x + 3, y + 3, 2, 2, ColorUtil.FromRgb(100, 116, 139));
            }
        }
    }

    public static void DrawMiniTaskbarDesk(
        PixelBuffer buf,
        int cx,
        int bottomY,
        int deskColor,
        AgentState state,
        float timeSeconds,
        AgentActionKind actionKind = AgentActionKind.None,
        DeskRigKind deskRig = DeskRigKind.Default,
        int behaviorVariant = 0)
    {
        int w = 28;
        int x = cx - w / 2;
        int topY = bottomY - 9;

        int effectiveDesk = deskRig switch
        {
            DeskRigKind.RgbStreamerRig => ColorUtil.FromRgb(30, 32, 44),
            DeskRigKind.CozyLofiRig => ColorUtil.FromRgb(146, 98, 58),
            DeskRigKind.RetroCrtRig => ColorUtil.FromRgb(176, 162, 134),
            DeskRigKind.CyberConsoleRig => ColorUtil.FromRgb(20, 26, 42),
            _ => deskColor
        };

        buf.FillRect(x - 1, topY - 1, w + 2, 10, Outline);
        buf.FillRect(x, topY, w, 3, effectiveDesk);
        buf.FillRect(x, topY + 3, w, 6, ColorUtil.Darken(effectiveDesk, 0.35f));

        // Tiny color-coded LED dot on mini desk
        if (state == AgentState.Working)
        {
            int ledColor = actionKind switch
            {
                AgentActionKind.Reading => ColorUtil.FromRgb(56, 189, 248),
                AgentActionKind.Searching => ColorUtil.FromRgb(34, 211, 238),
                AgentActionKind.Coding => ColorUtil.FromRgb(250, 204, 21),
                AgentActionKind.Executing => ColorUtil.FromRgb(34, 197, 94),
                AgentActionKind.Testing => ColorUtil.FromRgb(34, 197, 94),
                AgentActionKind.Building => ColorUtil.FromRgb(245, 158, 11),
                AgentActionKind.GitOps => ColorUtil.FromRgb(253, 224, 71),
                AgentActionKind.Reviewing => ColorUtil.FromRgb(244, 114, 182),
                AgentActionKind.Thinking => ColorUtil.FromRgb(148, 163, 184),
                _ => ColorUtil.FromRgb(34, 211, 238)
            };
            buf.FillRect(x + 2, topY + 1, 2, 2, ledColor);
        }

        switch (deskRig)
        {
            case DeskRigKind.DualMonitorRig:
                DrawMonitor(buf, cx - 13, topY - 5, 11, 6, state, timeSeconds, 0, actionKind, behaviorVariant);
                DrawMonitor(buf, cx + 2, topY - 5, 11, 6, state, timeSeconds, 2, actionKind, behaviorVariant);
                buf.FillRect(x + 4, topY + 5, w - 8, 1, ColorUtil.FromRgb(34, 211, 238));
                break;

            case DeskRigKind.RgbStreamerRig:
                // Streamer curved monitor + mini glowing RGB PC tower on right + red mic LED on left + RGB bar
                DrawMonitor(buf, cx - 11, topY - 5, 15, 6, state, timeSeconds, 0, actionKind, behaviorVariant);
                buf.FillRect(cx - 10, topY - 4, 13, 1, ColorUtil.FromRgb(220, 38, 38));
                // Mini mic pop dot on left
                buf.FillRect(x + 1, topY - 3, 2, 3, Outline);
                buf.SetPixel(x + 1, topY - 3, ColorUtil.FromRgb(239, 68, 68));
                // Mini glowing RGB PC tower on right
                buf.FillRect(cx + 6, topY - 6, 7, 8, Outline);
                buf.FillRect(cx + 7, topY - 5, 5, 6, ColorUtil.FromRgb(20, 22, 32));
                buf.FillRect(cx + 8, topY - 4, 3, 2, GetCyclingRgbColor(timeSeconds, 0));
                buf.FillRect(cx + 8, topY - 1, 3, 1, GetCyclingRgbColor(timeSeconds, 3));
                // Cycling RGB under-desk bar
                for (int seg = 0; seg < 4; seg++)
                {
                    buf.FillRect(x + 2 + seg * 6, topY + 5, 6, 2, GetCyclingRgbColor(timeSeconds, seg));
                }
                break;

            case DeskRigKind.CozyLofiRig:
                // Silver laptop + tiny potted succulent left + warm mug right
                DrawMonitor(buf, cx - 7, topY - 4, 14, 6, state, timeSeconds, 1, actionKind, behaviorVariant);
                buf.FillRect(x + 2, topY - 3, 4, 4, ColorUtil.FromRgb(194, 102, 54));
                buf.FillRect(x + 2, topY - 5, 4, 2, ColorUtil.FromRgb(74, 222, 128));
                buf.FillRect(x + w - 6, topY - 3, 4, 4, ColorUtil.FromRgb(226, 232, 240));
                buf.SetPixel(x + w - 5, topY - 4, ColorUtil.FromRgb(251, 191, 36));
                buf.FillRect(cx - 3, topY + 5, 6, 2, ColorUtil.FromRgb(217, 152, 82));
                break;

            case DeskRigKind.RetroCrtRig:
                // Chunky cream CRT box with phosphor green screen + blue floppy on right
                buf.FillRect(cx - 10, topY - 6, 14, 8, Outline);
                buf.FillRect(cx - 9, topY - 5, 12, 6, ColorUtil.FromRgb(224, 214, 188));
                buf.FillRect(cx - 8, topY - 4, 9, 4, ColorUtil.FromRgb(10, 32, 20));
                buf.FillRect(cx - 7, topY - 3, 6, 1, ColorUtil.FromRgb(74, 222, 128));
                buf.FillRect(cx - 7, topY - 1, 4, 1, ColorUtil.FromRgb(74, 222, 128));
                buf.FillRect(cx + 6, topY - 2, 5, 4, ColorUtil.FromRgb(37, 99, 235));
                buf.FillRect(cx + 7, topY - 2, 3, 1, ColorUtil.FromRgb(226, 232, 240));
                break;

            case DeskRigKind.CyberConsoleRig:
                // Obsidian HUD monitor + floating holo bar + cyan/magenta LED strips
                DrawMonitor(buf, cx - 8, topY - 4, 16, 6, state, timeSeconds, 0, actionKind, behaviorVariant);
                buf.BlendRect(cx - 6, topY - 7, 12, 2, ColorUtil.FromRgb(34, 211, 238), 140);
                buf.FillRect(x + 2, topY + 5, 11, 2, ColorUtil.FromRgb(34, 211, 238));
                buf.FillRect(x + 15, topY + 5, 11, 2, ColorUtil.FromRgb(236, 72, 153));
                break;

            default:
                // Low-profile glowing laptop screen
                DrawMonitor(buf, cx - 7, topY - 4, 14, 6, state, timeSeconds, 0, actionKind, behaviorVariant);
                break;
        }
    }

    public static void DrawBurningLaptop(PixelBuffer buf, int cx, int bottomY, float timeSeconds)
    {
        int x = cx - 8;
        int y = bottomY - 9;
        int chassis = ColorUtil.FromRgb(51, 65, 85);
        int chassisHi = ColorUtil.FromRgb(100, 116, 139);
        int screenErr = ColorUtil.FromRgb(153, 27, 27);

        buf.FillRect(x, y + 5, 16, 4, Outline);
        buf.FillRect(x + 1, y + 6, 14, 2, chassisHi);
        buf.FillRect(x + 2, y, 12, 7, Outline);
        buf.FillRect(x + 3, y + 1, 10, 5, chassis);
        buf.FillRect(x + 4, y + 2, 8, 3, screenErr);

        int f = (int)(timeSeconds * 14f) % 3;
        int red = ColorUtil.FromRgb(220, 38, 38);
        int orange = ColorUtil.FromRgb(249, 115, 22);
        int yellow = ColorUtil.FromRgb(254, 240, 138);

        buf.FillRect(x + 3, y - 3 - f, 10, 5 + f, red);
        buf.FillRect(x + 4, y - 5 + (f == 1 ? 1 : 0), 3, 4, red);
        buf.FillRect(x + 9, y - 4 - (f == 2 ? 1 : 0), 3, 4, red);
        buf.FillRect(x + 4, y - 2 - (f & 1), 8, 4, orange);
        buf.FillRect(x + 6, y - 1, 4, 3, yellow);

        buf.BlendRect(x - 1, y - 4, 18, 12, orange, 40);

        int smokeRise = (int)(timeSeconds * 8f) % 6;
        buf.BlendRect(x + 5, y - 8 - smokeRise, 4, 3, ColorUtil.FromRgb(100, 116, 139), 150);
        buf.BlendRect(x + 8, y - 11 - ((smokeRise + 3) % 6), 3, 3, ColorUtil.FromRgb(71, 85, 105), 115);
    }

    public static void DrawStatusBubble(
        PixelBuffer buf,
        int cx,
        int bottomY,
        ReadOnlySpan<char> label,
        int fillRgb,
        int borderRgb,
        int textRgb)
    {
        int textW = PixelFont.MeasureWidth(label, PixelFontSize.Small);
        int w = Math.Max(14, textW + 6);
        int h = 10;
        int x = cx - w / 2;
        int y = bottomY - h - 2;

        buf.DrawPixelCard(x, y, w, h, fillRgb, borderRgb);
        buf.FillRect(cx - 1, y + h, 3, 1, borderRgb);
        buf.SetPixel(cx, y + h, fillRgb);
        buf.SetPixel(cx, y + h + 1, borderRgb);

        PixelFont.DrawTextCentered(buf, cx, y + 3, label, textRgb, PixelFontSize.Small);
    }

    public static string NormalizeSpriteKey(string? spriteKey)
    {
        if (string.IsNullOrWhiteSpace(spriteKey)) return "generic";
        string trimmed = spriteKey.Trim();
        if (trimmed.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        if (string.Equals(trimmed, "shikamaru", StringComparison.OrdinalIgnoreCase)) return "shikamaru";
        if (string.Equals(trimmed, "minato", StringComparison.OrdinalIgnoreCase)) return "minato";
        if (string.Equals(trimmed, "tsunade", StringComparison.OrdinalIgnoreCase)) return "tsunade";
        if (string.Equals(trimmed, "kakashi", StringComparison.OrdinalIgnoreCase)) return "kakashi";
        if (string.Equals(trimmed, "mixi", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "domixi", StringComparison.OrdinalIgnoreCase)) return "mixi";
        if (string.Equals(trimmed, "neko-dev", StringComparison.OrdinalIgnoreCase)) return "neko-dev";
        if (string.Equals(trimmed, "cyber-hacker", StringComparison.OrdinalIgnoreCase)) return "cyber-hacker";
        if (string.Equals(trimmed, "ronin-dev", StringComparison.OrdinalIgnoreCase)) return "ronin-dev";
        if (string.Equals(trimmed, "astro-dev", StringComparison.OrdinalIgnoreCase)) return "astro-dev";
        if (string.Equals(trimmed, "wizard-dev", StringComparison.OrdinalIgnoreCase)) return "wizard-dev";
        if (string.Equals(trimmed, "mecha-droid", StringComparison.OrdinalIgnoreCase)) return "mecha-droid";
        if (string.Equals(trimmed, "street-gamer", StringComparison.OrdinalIgnoreCase)) return "street-gamer";
        if (string.Equals(trimmed, "clone", StringComparison.OrdinalIgnoreCase)) return "clone";
        if (string.Equals(trimmed, "omp", StringComparison.OrdinalIgnoreCase)) return "omp";
        if (string.Equals(trimmed, "agy", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "antigravity", StringComparison.OrdinalIgnoreCase)) return "agy";
        if (string.Equals(trimmed, "claude", StringComparison.OrdinalIgnoreCase)) return "claude";
        if (string.Equals(trimmed, "codex", StringComparison.OrdinalIgnoreCase)) return "codex";
        return "generic";
    }
}
