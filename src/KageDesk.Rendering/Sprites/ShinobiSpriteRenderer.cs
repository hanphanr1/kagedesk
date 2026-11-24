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
        int variantSeed = 0)
    {
        string key = NormalizeSpriteKey(spriteKey);
        float t = timeSeconds + phaseOffset * 0.43f;

        // Motion & animation offsets
        int jumpY = 0;
        int shakeX = 0;
        int breathY = 0;
        bool armsUp = false;
        bool waveAlt = false;
        bool handsOnHead = false;
        bool typingFast = false;
        bool eyesBlink = false;
        bool eyesHappy = false;
        bool eyesPanic = false;

        switch (state)
        {
            case AgentState.Idle:
                breathY = ((int)(t * 1.5f) & 1);
                float blinkCycle = t % 3.6f;
                eyesBlink = blinkCycle > 3.42f;
                break;

            case AgentState.Working:
                typingFast = true;
                breathY = ((int)(t * 6f) & 1);
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
        int torsoTop = footY - 13 + (jumpY == 0 ? breathY : 0);
        int headTop = torsoTop - 9;

        // 1. Back hair / cape layer (behind torso)
        DrawBackLayer(buf, key, x, headTop, torsoTop, footY, t);

        // 2. Legs & sandals (unless fully behind desk)
        if (!behindDesk)
        {
            int leftLegX = x - 4;
            int rightLegX = x + 1;
            int stepOffset = (state == AgentState.Done || state == AgentState.NeedsApproval) && jumpY < -1 ? -1 : 0;

            // Legs outline & fill
            buf.FillRect(leftLegX - 1, legsTop, 5, 5 + stepOffset, Outline);
            buf.FillRect(rightLegX - 1, legsTop, 5, 5 + stepOffset, Outline);
            buf.FillRect(leftLegX, legsTop, 3, 3 + stepOffset, ShinobiPants);
            buf.FillRect(rightLegX, legsTop, 3, 3 + stepOffset, ShinobiPants);
            buf.FillRect(leftLegX, footY - 2 + stepOffset, 3, 2, SandalDark);
            buf.FillRect(rightLegX, footY - 2 + stepOffset, 3, 2, SandalDark);
        }

        // 3. Torso / Outfit
        DrawTorso(buf, key, x, torsoTop, variantSeed);

        // 4. Arms (idle, typing, waving, victory, panic)
        DrawArms(buf, key, x, torsoTop, headTop, armsUp, waveAlt, handsOnHead, typingFast, t, state, variantSeed);

        // 5. Head, Face, Eyes, Hair
        DrawHeadAndHair(buf, key, x, headTop, state, eyesBlink, eyesHappy, eyesPanic, t, variantSeed);

        // 6. Special character-held idle props (Kakashi's tiny orange book)
        if (key == "kakashi" && state == AgentState.Idle)
        {
            DrawTinyOrangeBook(buf, x + 4, torsoTop + 1);
        }

        // 7. Sweat drop on panic/failed
        if (eyesPanic)
        {
            int dropY = headTop + 2 + ((int)(t * 6f) & 1);
            buf.SetPixel(x + 7, dropY, ColorUtil.FromRgb(56, 189, 248));
            buf.SetPixel(x + 7, dropY + 1, ColorUtil.FromRgb(186, 230, 253));
        }

        // 8. Subtle cyan chakra aura glint on Minato working or Clone
        if (key == "minato" && state == AgentState.Working)
        {
            int sparkIdx = (int)(t * 10f) % 4;
            int sx = sparkIdx < 2 ? x - 8 : x + 7;
            int sy = headTop + 2 + (sparkIdx & 1) * 5;
            buf.BlendPixel(sx, sy, ColorUtil.FromRgb(254, 240, 138), 220);
            buf.BlendPixel(sx + 1, sy - 1, ColorUtil.FromRgb(56, 189, 248), 190);
        }
        else if (key == "clone")
        {
            int rimAlpha = 95 + (int)(MathF.Sin(t * 5f) * 35f);
            buf.BlendPixel(x - 7, torsoTop + 2, ColorUtil.FromRgb(56, 189, 248), rimAlpha);
            buf.BlendPixel(x + 6, headTop + 4, ColorUtil.FromRgb(56, 189, 248), rimAlpha);
        }
    }

    private static void DrawBackLayer(PixelBuffer buf, string key, int x, int headTop, int torsoTop, int footY, float t)
    {
        if (key == "shikamaru")
        {
            // Iconic high spiky pineapple ponytail rising up-right/back
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
            // White Hokage cloak shoulders & flared hem with crimson flames
            int cloak = ColorUtil.FromRgb(241, 245, 249);
            int cloakShade = ColorUtil.FromRgb(203, 213, 225);
            int flameRed = ColorUtil.FromRgb(220, 38, 38);
            int flameBright = ColorUtil.FromRgb(239, 68, 68);
            int cloakH = Math.Min(11, Math.Max(7, footY - torsoTop - 1));
            buf.FillRect(x - 7, torsoTop, 14, cloakH + 1, Outline);
            buf.FillRect(x - 6, torsoTop + 1, 12, cloakH - 1, cloak);
            buf.FillRect(x - 6, torsoTop + 2, 2, cloakH - 3, cloakShade);
            buf.FillRect(x + 4, torsoTop + 2, 2, cloakH - 3, cloakShade);

            // Red flame pattern along bottom hem
            int hemY = torsoTop + cloakH - 2;
            buf.FillRect(x - 6, hemY, 12, 2, flameRed);
            for (int fx = x - 5; fx <= x + 4; fx += 3)
            {
                buf.SetPixel(fx, hemY - 1, flameBright);
                buf.SetPixel(fx + 1, hemY, flameBright);
            }
        }
    }

    private static void DrawTorso(PixelBuffer buf, string key, int x, int torsoTop, int variantSeed)
    {
        // Base silhouette 12x9
        buf.FillRect(x - 6, torsoTop - 1, 12, 10, Outline);

        switch (key)
        {
            case "shikamaru":
            case "kakashi":
                // Dark undershirt + Konoha jonin tactical vest
                buf.FillRect(x - 5, torsoTop, 10, 8, ShinobiDark);
                buf.FillRect(x - 4, torsoTop, 8, 7, KonohaVest);
                buf.FillRect(x - 4, torsoTop + 5, 8, 2, KonohaVestShade);
                // Vest chest pockets & zipper
                buf.FillRect(x - 3, torsoTop + 1, 2, 2, KonohaVestHi);
                buf.FillRect(x + 1, torsoTop + 1, 2, 2, KonohaVestHi);
                buf.FillRect(x, torsoTop, 1, 7, KonohaVestShade);
                break;

            case "minato":
                // Deep navy shinobi tunic visible at center with white flame-hem cloak lapels
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
                // Sage-green haori open over cream top with dark obi sash
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
                // Indigo/cyan chakra tactical uniform
                int indigo = ColorUtil.FromRgb(49, 46, 129);
                int indigoDark = ColorUtil.FromRgb(30, 27, 75);
                int cyanTrim = ColorUtil.FromRgb(34, 211, 238);
                buf.FillRect(x - 5, torsoTop, 10, 8, indigo);
                buf.FillRect(x - 5, torsoTop + 5, 10, 3, indigoDark);
                buf.FillRect(x - 3, torsoTop + 1, 6, 1, cyanTrim);
                buf.FillRect(x, torsoTop + 1, 1, 6, cyanTrim);
                break;

            default:
                // Generic hooded developer (emerald or purple hoodie)
                bool purple = (variantSeed & 1) == 1;
                int hoodie = purple ? ColorUtil.FromRgb(124, 58, 237) : ColorUtil.FromRgb(16, 185, 129);
                int hoodieShade = purple ? ColorUtil.FromRgb(91, 33, 182) : ColorUtil.FromRgb(5, 150, 105);
                int stringCol = ColorUtil.FromRgb(226, 232, 240);
                buf.FillRect(x - 5, torsoTop, 10, 8, hoodie);
                buf.FillRect(x - 5, torsoTop + 5, 10, 3, hoodieShade);
                // Hoodie drawstrings & front pocket
                buf.FillRect(x - 2, torsoTop + 1, 1, 3, stringCol);
                buf.FillRect(x + 1, torsoTop + 1, 1, 3, stringCol);
                buf.FillRect(x - 3, torsoTop + 5, 6, 2, hoodie);
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
        int variantSeed)
    {
        int sleeveColor = key switch
        {
            "minato" => ColorUtil.FromRgb(241, 245, 249),
            "tsunade" => ColorUtil.FromRgb(77, 139, 91),
            "clone" => ColorUtil.FromRgb(34, 211, 238),
            "generic" => (variantSeed & 1) == 1 ? ColorUtil.FromRgb(124, 58, 237) : ColorUtil.FromRgb(16, 185, 129),
            _ => ShinobiDark
        };

        int handColor = key == "clone" ? ColorUtil.FromRgb(165, 243, 252) : SkinBase;

        if (handsOnHead)
        {
            // Both hands clutching sides of head in panic
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

            // Left arm raised
            buf.FillRect(x - 8, torsoTop - 4 - leftLift, 3, 7, Outline);
            buf.FillRect(x - 7, torsoTop - 3 - leftLift, 2, 5, sleeveColor);
            buf.FillRect(x - 7, torsoTop - 5 - leftLift, 2, 2, handColor);

            // Right arm raised
            buf.FillRect(x + 5, torsoTop - 4 - rightLift, 3, 7, Outline);
            buf.FillRect(x + 5, torsoTop - 3 - rightLift, 2, 5, sleeveColor);
            buf.FillRect(x + 5, torsoTop - 5 - rightLift, 2, 2, handColor);
            return;
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
        int variantSeed)
    {
        // Head outline 12x10
        buf.FillRect(x - 6, headTop - 1, 12, 10, Outline);

        int faceColor = key == "clone" ? ColorUtil.FromRgb(186, 230, 253) : SkinBase;
        int faceShade = key == "clone" ? ColorUtil.FromRgb(125, 211, 252) : SkinShade;
        int faceHi = key == "clone" ? ColorUtil.FromRgb(224, 242, 254) : SkinHighlight;

        buf.FillRect(x - 5, headTop, 10, 8, faceColor);
        buf.FillRect(x - 5, headTop + 7, 10, 1, faceShade);
        buf.SetPixel(x - 4, headTop + 3, faceHi);

        // Eyes
        int eyeY = headTop + 4;
        if (eyesHappy)
        {
            // ^ ^ happy eyes
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
            // Open panic mouth
            buf.FillRect(x - 1, headTop + 6, 2, 2, ColorUtil.FromRgb(153, 27, 27));
        }
        else
        {
            // Expressive 2x2 pixel eyes with glint
            buf.FillRect(x - 3, eyeY, 2, 2, EyeDark);
            buf.SetPixel(x - 3, eyeY, EyeWhite);

            if (key == "kakashi" && (state == AgentState.Working || state == AgentState.Failed))
            {
                // Glowing crimson Sharingan eye!
                buf.FillRect(x + 1, eyeY, 2, 2, SharinganRed);
                buf.SetPixel(x + 1, eyeY, SharinganGlow);
                int pulse = 110 + (int)(MathF.Sin(t * 8f) * 50f);
                buf.BlendPixel(x + 3, eyeY, SharinganRed, pulse);
            }
            else
            {
                buf.FillRect(x + 1, eyeY, 2, 2, EyeDark);
                buf.SetPixel(x + 1, eyeY, EyeWhite);
            }
        }

        // Character-specific hair, mask, headband, forehead mark
        switch (key)
        {
            case "shikamaru":
                {
                    int hair = ColorUtil.FromRgb(24, 28, 38);
                    int hairHi = ColorUtil.FromRgb(56, 64, 84);
                    // Hair crown + sideburns
                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 5, headTop - 1, 10, 3, hair);
                    buf.FillRect(x - 3, headTop - 1, 4, 1, hairHi);
                    buf.FillRect(x - 5, headTop + 2, 1, 3, hair);
                    buf.FillRect(x + 4, headTop + 2, 1, 3, hair);
                    // Headband
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

                    // Spiky yellow hair spikes!
                    buf.FillRect(x - 7, headTop - 2, 14, 4, Outline);
                    buf.FillRect(x - 5, headTop - 4, 3, 2, Outline);
                    buf.FillRect(x - 1, headTop - 5, 3, 3, Outline);
                    buf.FillRect(x + 3, headTop - 4, 3, 2, Outline);

                    buf.FillRect(x - 6, headTop - 1, 12, 3, gold);
                    buf.FillRect(x - 4, headTop - 3, 2, 2, gold);
                    buf.FillRect(x, headTop - 4, 2, 3, goldHi);
                    buf.FillRect(x + 3, headTop - 3, 2, 2, gold);
                    buf.FillRect(x - 5, headTop - 1, 8, 1, goldHi);

                    // Iconic long cheek-framing side bangs!
                    buf.FillRect(x - 6, headTop + 2, 2, 6, goldShade);
                    buf.FillRect(x + 4, headTop + 2, 2, 6, goldShade);
                    buf.SetPixel(x - 5, headTop + 2, gold);
                    buf.SetPixel(x + 4, headTop + 2, gold);

                    // Headband
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
                    // Parted bangs framing forehead + side locks
                    buf.FillRect(x - 6, headTop + 2, 2, 6, blonde);
                    buf.FillRect(x + 4, headTop + 2, 2, 6, blonde);
                    buf.SetPixel(x - 6, headTop + 6, blondeShade);
                    buf.SetPixel(x + 5, headTop + 6, blondeShade);

                    // Iconic violet forehead diamond (Strength of a Hundred Seal)!
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

                    // Gravity-defying tilted spiky silver hair
                    buf.FillRect(x - 7, headTop - 3, 13, 5, Outline);
                    buf.FillRect(x - 6, headTop - 5, 4, 2, Outline);
                    buf.FillRect(x - 2, headTop - 6, 5, 3, Outline);
                    buf.FillRect(x + 2, headTop - 4, 4, 2, Outline);

                    buf.FillRect(x - 6, headTop - 2, 11, 3, silver);
                    buf.FillRect(x - 5, headTop - 4, 3, 2, silverHi);
                    buf.FillRect(x - 1, headTop - 5, 3, 3, silverHi);
                    buf.FillRect(x + 2, headTop - 3, 3, 2, silver);
                    buf.FillRect(x - 5, headTop, 10, 1, silverShade);

                    // Slanted headband covering left eye when Idle/Done, lifted when Working/Failed
                    buf.FillRect(x - 5, headTop + 1, 10, 2, HeadbandCloth);
                    buf.FillRect(x - 2, headTop + 1, 4, 2, HeadbandMetal);
                    if (state != AgentState.Working && state != AgentState.Failed)
                    {
                        buf.FillRect(x + 1, headTop + 3, 4, 2, HeadbandCloth);
                    }

                    // Iconic dark ninja face mask covering lower face!
                    buf.FillRect(x - 5, headTop + 6, 10, 3, mask);
                    buf.FillRect(x - 4, headTop + 6, 8, 1, maskHi);
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
                    // Lower ninja mask
                    buf.FillRect(x - 5, headTop + 6, 10, 2, hood);
                    break;
                }

            default:
                {
                    bool purple = (variantSeed & 1) == 1;
                    int hood = purple ? ColorUtil.FromRgb(109, 40, 217) : ColorUtil.FromRgb(16, 185, 129);
                    int hoodHi = purple ? ColorUtil.FromRgb(167, 139, 250) : ColorUtil.FromRgb(110, 231, 183);
                    int visor = ColorUtil.FromRgb(56, 189, 248);

                    // Cozy rounded hood framing face
                    buf.FillRect(x - 6, headTop - 2, 12, 4, Outline);
                    buf.FillRect(x - 6, headTop + 1, 2, 7, Outline);
                    buf.FillRect(x + 4, headTop + 1, 2, 7, Outline);

                    buf.FillRect(x - 5, headTop - 1, 10, 3, hood);
                    buf.FillRect(x - 4, headTop - 1, 8, 1, hoodHi);
                    buf.FillRect(x - 5, headTop + 2, 1, 6, hood);
                    buf.FillRect(x + 4, headTop + 2, 1, 6, hood);

                    // Glowing pixel glasses reflecting screen
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
        // Handle
        buf.SetPixel(x + 6, y + 2, mug);

        // Animated rising steam pixels
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
        float timeSeconds)
    {
        int deskW = 48;
        int x = cx - deskW / 2;
        int topHighlight = ColorUtil.Lighten(deskTopColor, 0.16f);
        int frontShade = ColorUtil.Darken(deskFrontColor, 0.25f);

        // Floor shadow under desk
        buf.BlendRect(x - 1, deskTopY + 18, deskW + 2, 3, ColorUtil.FromRgb(8, 10, 16), 115);

        // Desk silhouette & wooden/steel surface
        buf.FillRect(x - 1, deskTopY - 1, deskW + 2, 20, Outline);
        buf.FillRect(x, deskTopY, deskW, 6, deskTopColor);
        buf.FillRect(x, deskTopY, deskW, 1, topHighlight);
        buf.FillRect(x, deskTopY + 6, deskW, 12, deskFrontColor);
        buf.FillRect(x, deskTopY + 6, deskW, 1, frontShade);

        // Side legs/recess panel
        buf.FillRect(x + 4, deskTopY + 8, deskW - 8, 9, frontShade);
        buf.FillRect(x + 5, deskTopY + 9, deskW - 10, 7, ColorUtil.Darken(deskFrontColor, 0.12f));

        // Failed state -> burning laptop offset slightly forward-left so panicking face stays visible!
        if (state == AgentState.Failed)
        {
            DrawBurningLaptop(buf, cx - 10, deskTopY + 5, timeSeconds);
            return;
        }

        switch (stationType)
        {
            case "shikamaru":
                DrawScrollDeskProps(buf, cx, deskTopY, state, timeSeconds);
                break;
            case "minato":
                DrawDualMonitorProps(buf, cx, deskTopY, state, timeSeconds);
                break;
            case "tsunade":
                DrawChakraLabProps(buf, cx, deskTopY, state, timeSeconds);
                break;
            case "kakashi":
                DrawReviewDeskProps(buf, cx, deskTopY, state, timeSeconds);
                break;
            default:
                DrawCloneTerminalProps(buf, cx, deskTopY, state, timeSeconds);
                break;
        }
    }

    private static void DrawScrollDeskProps(PixelBuffer buf, int cx, int deskTopY, AgentState state, float timeSeconds)
    {
        // Unrolled glowing command scroll
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

        // Tactical rune lines on scroll
        int anim = (int)(timeSeconds * 4f) % 4;
        buf.FillRect(sx + 3, sy + 1, 7 + (state == AgentState.Working ? anim : 0), 1, runeColor);
        buf.FillRect(sx + 3, sy + 3, 14, 1, runeColor);
        buf.FillRect(sx + 3, sy + 4, 9, 1, ColorUtil.Darken(runeColor, 0.3f));

        // Steaming coffee cup on right side of command desk
        DrawCoffeeCup(buf, cx + 14, deskTopY - 3, timeSeconds);
    }

    private static void DrawDualMonitorProps(PixelBuffer buf, int cx, int deskTopY, AgentState state, float timeSeconds)
    {
        // Angled dual monitors on left & right wings so Minato's face & Hokage cloak stay clearly visible in the center!
        DrawMonitor(buf, cx - 22, deskTopY - 8, 15, 9, state, timeSeconds, 0);
        DrawMonitor(buf, cx + 7, deskTopY - 8, 15, 9, state, timeSeconds, 2);

        // Keyboard
        buf.FillRect(cx - 7, deskTopY + 2, 14, 3, ColorUtil.FromRgb(30, 41, 59));
        buf.FillRect(cx - 6, deskTopY + 3, 12, 1, ColorUtil.FromRgb(100, 116, 139));
    }

    private static void DrawChakraLabProps(PixelBuffer buf, int cx, int deskTopY, AgentState state, float timeSeconds)
    {
        // Lab test monitor on far left
        DrawMonitor(buf, cx - 22, deskTopY - 8, 15, 9, state, timeSeconds, 1);

        // Bubbling green chakra flasks on right!
        int fx = cx + 8;
        int fy = deskTopY - 7;
        int glass = ColorUtil.FromRgb(203, 213, 225);
        int chakra = ColorUtil.FromRgb(34, 197, 94);
        int chakraHi = ColorUtil.FromRgb(134, 239, 172);

        // Erlenmeyer flask
        buf.FillRect(fx + 1, fy, 4, 4, Outline);
        buf.FillRect(fx - 1, fy + 3, 8, 6, Outline);
        buf.FillRect(fx + 2, fy + 1, 2, 3, glass);
        buf.FillRect(fx, fy + 4, 6, 4, chakra);
        buf.FillRect(fx + 1, fy + 4, 3, 1, chakraHi);

        // Second small vial
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

    private static void DrawReviewDeskProps(PixelBuffer buf, int cx, int deskTopY, AgentState state, float timeSeconds)
    {
        // Code-diff review monitor on left wing so Kakashi's mask, Sharingan eye & orange book are 100% unobstructed!
        DrawMonitor(buf, cx - 22, deskTopY - 8, 16, 9, state, timeSeconds, 3);

        // Neat scroll stack on far right
        int parchment = ColorUtil.FromRgb(236, 224, 196);
        int tie = ColorUtil.FromRgb(220, 38, 38);
        buf.FillRect(cx + 13, deskTopY - 3, 8, 5, Outline);
        buf.FillRect(cx + 14, deskTopY - 2, 6, 3, parchment);
        buf.FillRect(cx + 17, deskTopY - 2, 1, 3, tie);

        // Tiny orange book resting on desk when working
        if (state != AgentState.Idle)
        {
            DrawTinyOrangeBook(buf, cx + 6, deskTopY - 2);
        }
    }

    private static void DrawCloneTerminalProps(PixelBuffer buf, int cx, int deskTopY, AgentState state, float timeSeconds)
    {
        // Side-mounted tactical terminal so Clone headband & Generic hooded dev face remain completely clear!
        DrawMonitor(buf, cx - 21, deskTopY - 7, 15, 8, state, timeSeconds, 0);
        // Compact keyboard in center
        buf.FillRect(cx - 4, deskTopY + 2, 14, 3, ColorUtil.FromRgb(30, 41, 59));
        buf.FillRect(cx - 3, deskTopY + 3, 12, 1, ColorUtil.FromRgb(56, 189, 248));
        // Glowing cyan chakra strip on front of clone desk
        int stripColor = state == AgentState.Working
            ? ColorUtil.FromRgb(34, 211, 238)
            : ColorUtil.FromRgb(71, 85, 105);
        buf.FillRect(cx - 14, deskTopY + 11, 28, 2, stripColor);
    }

    private static void DrawMonitor(
        PixelBuffer buf,
        int x,
        int y,
        int w,
        int h,
        AgentState state,
        float timeSeconds,
        int screenMode)
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

        if (state == AgentState.Working)
        {
            int scroll = (int)(timeSeconds * 8f);
            int c1 = screenMode switch
            {
                1 => ColorUtil.FromRgb(74, 222, 128),
                3 => ColorUtil.FromRgb(248, 113, 113),
                _ => ColorUtil.FromRgb(56, 189, 248)
            };
            int c2 = screenMode switch
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
            // Subtle screen glow cast onto desk
            buf.BlendRect(x + 1, y + h, w - 2, 3, c1, 35);
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
            // Calm standby cursor
            if (((int)(timeSeconds * 2f) & 1) == 0)
            {
                buf.FillRect(x + 3, y + 3, 2, 2, ColorUtil.FromRgb(100, 116, 139));
            }
        }
    }

    public static void DrawMiniTaskbarDesk(PixelBuffer buf, int cx, int bottomY, int deskColor, AgentState state, float timeSeconds)
    {
        int w = 28;
        int x = cx - w / 2;
        int topY = bottomY - 9;

        buf.FillRect(x - 1, topY - 1, w + 2, 10, Outline);
        buf.FillRect(x, topY, w, 3, deskColor);
        buf.FillRect(x, topY + 3, w, 6, ColorUtil.Darken(deskColor, 0.35f));

        // Low-profile glowing laptop screen so working character's face & outfit remain crisp!
        DrawMonitor(buf, cx - 7, topY - 4, 14, 6, state, timeSeconds, 0);
    }

    public static void DrawBurningLaptop(PixelBuffer buf, int cx, int bottomY, float timeSeconds)
    {
        int x = cx - 8;
        int y = bottomY - 9;
        int chassis = ColorUtil.FromRgb(51, 65, 85);
        int chassisHi = ColorUtil.FromRgb(100, 116, 139);
        int screenErr = ColorUtil.FromRgb(153, 27, 27);

        // Laptop base & screen
        buf.FillRect(x, y + 5, 16, 4, Outline);
        buf.FillRect(x + 1, y + 6, 14, 2, chassisHi);
        buf.FillRect(x + 2, y, 12, 7, Outline);
        buf.FillRect(x + 3, y + 1, 10, 5, chassis);
        buf.FillRect(x + 4, y + 2, 8, 3, screenErr);

        // Multi-layer flickering pixel flames!
        int f = (int)(timeSeconds * 14f) % 3;
        int red = ColorUtil.FromRgb(220, 38, 38);
        int orange = ColorUtil.FromRgb(249, 115, 22);
        int yellow = ColorUtil.FromRgb(254, 240, 138);

        buf.FillRect(x + 3, y - 3 - f, 10, 5 + f, red);
        buf.FillRect(x + 4, y - 5 + (f == 1 ? 1 : 0), 3, 4, red);
        buf.FillRect(x + 9, y - 4 - (f == 2 ? 1 : 0), 3, 4, red);
        buf.FillRect(x + 4, y - 2 - (f & 1), 8, 4, orange);
        buf.FillRect(x + 6, y - 1, 4, 3, yellow);

        // Warm fire halo
        buf.BlendRect(x - 1, y - 4, 18, 12, orange, 40);

        // Rising smoke puffs
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
        // Little downward pointer triangle
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

        return trimmed.ToLowerInvariant() switch
        {
            "shikamaru" => "shikamaru",
            "minato" => "minato",
            "tsunade" => "tsunade",
            "kakashi" => "kakashi",
            "clone" => "clone",
            _ => "generic"
        };
    }
}
