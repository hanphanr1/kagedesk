using System.Runtime.CompilerServices;

namespace KageDesk.Rendering.Canvas;

public static class ColorUtil
{
    public const int Transparent = 0x00000000;
    public const int Black = unchecked((int)0xFF000000);
    public const int White = unchecked((int)0xFFFFFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FromRgb(int r, int g, int b)
    {
        r = ClampByte(r);
        g = ClampByte(g);
        b = ClampByte(b);
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FromArgb(int a, int r, int g, int b)
    {
        a = ClampByte(a);
        r = ClampByte(r);
        g = ClampByte(g);
        b = ClampByte(b);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WithAlpha(int rgb, int alpha)
    {
        alpha = ClampByte(alpha);
        return (alpha << 24) | (rgb & 0x00FFFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Div255(int v)
    {
        int t = v + 128;
        return (t + (t >> 8)) >> 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToPremultiplied(int argb)
    {
        int a = (argb >> 24) & 0xFF;
        if (a == 255) return argb;
        if (a == 0) return 0;

        int r = Div255(((argb >> 16) & 0xFF) * a);
        int g = Div255(((argb >> 8) & 0xFF) * a);
        int b = Div255((argb & 0xFF) * a);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Premultiply(int a, int r, int g, int b)
    {
        a = ClampByte(a);
        if (a == 0) return 0;
        r = ClampByte(r);
        g = ClampByte(g);
        b = ClampByte(b);
        if (a == 255) return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

        int pr = Div255(r * a);
        int pg = Div255(g * a);
        int pb = Div255(b * a);
        return (a << 24) | (pr << 16) | (pg << 8) | pb;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BlendOverPremultiplied(int dstPArgb, int srcStraightArgb)
    {
        int sa = (srcStraightArgb >> 24) & 0xFF;
        if (sa == 0) return dstPArgb;
        if (sa == 255) return srcStraightArgb;

        int sr = (srcStraightArgb >> 16) & 0xFF;
        int sg = (srcStraightArgb >> 8) & 0xFF;
        int sb = srcStraightArgb & 0xFF;

        int srp = Div255(sr * sa);
        int sgp = Div255(sg * sa);
        int sbp = Div255(sb * sa);

        return BlendPremultipliedOverPremultiplied(dstPArgb, sa, srp, sgp, sbp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BlendPremultipliedOverPremultiplied(int dstPArgb, int sa, int srp, int sgp, int sbp)
    {
        int da = (dstPArgb >> 24) & 0xFF;
        if (da == 0)
        {
            return (sa << 24) | (srp << 16) | (sgp << 8) | sbp;
        }

        int invSa = 255 - sa;
        int dr = (dstPArgb >> 16) & 0xFF;
        int dg = (dstPArgb >> 8) & 0xFF;
        int db = dstPArgb & 0xFF;

        int oa = sa + Div255(da * invSa);
        int or_ = srp + Div255(dr * invSa);
        int og = sgp + Div255(dg * invSa);
        int ob = sbp + Div255(db * invSa);

        if (oa > 255) oa = 255;
        if (or_ > oa) or_ = oa;
        if (og > oa) og = oa;
        if (ob > oa) ob = oa;

        return (oa << 24) | (or_ << 16) | (og << 8) | ob;
    }

    public static int ParseHex(string? hex, int fallback = Black)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;

        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span.Slice(1);
        }

        if (span.Length == 6)
        {
            if (TryParseHexByte(span[0], span[1], out int r) &&
                TryParseHexByte(span[2], span[3], out int g) &&
                TryParseHexByte(span[4], span[5], out int b))
            {
                return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
            return fallback;
        }

        if (span.Length == 8)
        {
            if (TryParseHexByte(span[0], span[1], out int a) &&
                TryParseHexByte(span[2], span[3], out int r) &&
                TryParseHexByte(span[4], span[5], out int g) &&
                TryParseHexByte(span[6], span[7], out int b))
            {
                return (a << 24) | (r << 16) | (g << 8) | b;
            }
            return fallback;
        }

        return fallback;
    }

    public static int Lerp(int colorA, int colorB, float t)
    {
        if (t <= 0f) return colorA;
        if (t >= 1f) return colorB;

        int a1 = (colorA >> 24) & 0xFF;
        int r1 = (colorA >> 16) & 0xFF;
        int g1 = (colorA >> 8) & 0xFF;
        int b1 = colorA & 0xFF;

        int a2 = (colorB >> 24) & 0xFF;
        int r2 = (colorB >> 16) & 0xFF;
        int g2 = (colorB >> 8) & 0xFF;
        int b2 = colorB & 0xFF;

        int a = (int)(a1 + (a2 - a1) * t + 0.5f);
        int r = (int)(r1 + (r2 - r1) * t + 0.5f);
        int g = (int)(g1 + (g2 - g1) * t + 0.5f);
        int b = (int)(b1 + (b2 - b1) * t + 0.5f);

        return (ClampByte(a) << 24) | (ClampByte(r) << 16) | (ClampByte(g) << 8) | ClampByte(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Darken(int color, float amount) => Lerp(color, Black, amount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Lighten(int color, float amount) => Lerp(color, White, amount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Tint(int color, int tintColor, float amount) => Lerp(color, tintColor, amount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampByte(int value) => value < 0 ? 0 : (value > 255 ? 255 : value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseHexByte(char hi, char lo, out int value)
    {
        int h = HexNibble(hi);
        int l = HexNibble(lo);
        if ((h | l) < 0)
        {
            value = 0;
            return false;
        }
        value = (h << 4) | l;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexNibble(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}
