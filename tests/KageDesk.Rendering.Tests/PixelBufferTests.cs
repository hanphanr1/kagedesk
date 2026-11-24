using System.Drawing.Imaging;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Sprites;

namespace KageDesk.Rendering.Tests;

public class PixelBufferTests
{
    [Fact]
    public void Constructor_InitializesPinned32bppPArgbBitmap()
    {
        using var buf = new PixelBuffer(64, 32);

        Assert.Equal(64, buf.Width);
        Assert.Equal(32, buf.Height);
        Assert.Equal(PixelFormat.Format32bppPArgb, buf.Bitmap.PixelFormat);
        Assert.Equal(64, buf.Bitmap.Width);
        Assert.Equal(32, buf.Bitmap.Height);
    }

    [Fact]
    public void FillRect_ClipsSafelyOutOfBounds()
    {
        using var buf = new PixelBuffer(16, 16);
        buf.ClearTransparent();
        int red = ColorUtil.FromRgb(255, 0, 0);

        buf.FillRect(-5, -5, 10, 10, red);
        buf.FillRect(12, 12, 20, 20, red);

        Assert.Equal(red, buf.GetPixel(0, 0));
        Assert.Equal(red, buf.GetPixel(4, 4));
        Assert.Equal(0, buf.GetPixel(5, 5));
        Assert.Equal(red, buf.GetPixel(15, 15));
        Assert.Equal(0, buf.GetPixel(-1, -1));
        Assert.Equal(0, buf.GetPixel(16, 16));
    }

    [Fact]
    public void BlendRect_ProducesValidPremultipliedAlphaOverOpaqueAndTransparent()
    {
        using var buf = new PixelBuffer(8, 8);
        buf.ClearTransparent();

        // Semi-transparent white (50%) over transparent
        int halfWhite = ColorUtil.FromArgb(128, 255, 255, 255);
        buf.BlendRect(0, 0, 4, 4, halfWhite);

        int p = buf.GetPixel(1, 1);
        int a = (p >> 24) & 0xFF;
        int r = (p >> 16) & 0xFF;
        int g = (p >> 8) & 0xFF;
        int b = p & 0xFF;

        Assert.Equal(128, a);
        Assert.InRange(r, 127, 128);
        Assert.Equal(r, g);
        Assert.Equal(r, b);

        // Blend over opaque black
        buf.FillRect(4, 0, 4, 4, ColorUtil.Black);
        buf.BlendRect(4, 0, 4, 4, halfWhite);
        int p2 = buf.GetPixel(5, 1);
        Assert.Equal(255, (p2 >> 24) & 0xFF);
        Assert.InRange((p2 >> 16) & 0xFF, 127, 129);
    }

    [Fact]
    public void DrawLine_And_DrawCircle_And_DrawOutlineRect_RenderPixels()
    {
        using var buf = new PixelBuffer(32, 32);
        buf.ClearTransparent();

        int cyan = ColorUtil.FromRgb(34, 211, 238);
        int gold = ColorUtil.FromRgb(250, 204, 21);
        int green = ColorUtil.FromRgb(34, 197, 94);

        buf.DrawLine(0, 0, 10, 10, cyan);
        Assert.Equal(cyan, buf.GetPixel(0, 0));
        Assert.Equal(cyan, buf.GetPixel(5, 5));
        Assert.Equal(cyan, buf.GetPixel(10, 10));

        buf.DrawCircle(20, 20, 4, gold, filled: true);
        Assert.Equal(gold, buf.GetPixel(20, 20));
        Assert.Equal(gold, buf.GetPixel(24, 20));
        Assert.Equal(0, buf.GetPixel(26, 20));

        buf.DrawOutlineRect(2, 20, 8, 8, green);
        Assert.Equal(green, buf.GetPixel(2, 20));
        Assert.Equal(green, buf.GetPixel(9, 27));
        Assert.Equal(0, buf.GetPixel(5, 24));
    }

    [Fact]
    public void ColorUtil_ParseHex_HandlesRgbAndArgbAndInvalidFallback()
    {
        Assert.Equal(ColorUtil.FromRgb(0x17, 0x1B, 0x28), ColorUtil.ParseHex("#171b28"));
        Assert.Equal(ColorUtil.FromArgb(0x80, 0xFF, 0x00, 0x40), ColorUtil.ParseHex("#80FF0040"));
        Assert.Equal(ColorUtil.White, ColorUtil.ParseHex("invalid", ColorUtil.White));
    }

    [Fact]
    public void PixelFont_DrawsBothSizesAndMeasuresCorrectly()
    {
        using var buf = new PixelBuffer(128, 32);
        buf.ClearTransparent();

        Assert.Equal(4 * 4 - 1, PixelFont.MeasureWidth("PASS", PixelFontSize.Small));
        Assert.Equal(4 * 6 - 1, PixelFont.MeasureWidth("PASS", PixelFontSize.Regular));

        PixelFont.DrawText(buf, 2, 2, "ACCOUNTS: 98% [OK]!", ColorUtil.White, PixelFontSize.Small);
        PixelFont.DrawText(buf, 2, 12, "OPEN ORCA · 02:15", ColorUtil.FromRgb(245, 158, 11), PixelFontSize.Regular);

        int litPixels = 0;
        foreach (int px in buf.ReadOnlyPixels)
        {
            if (px != 0) litPixels++;
        }

        Assert.True(litPixels > 120, $"Expected >120 lit font pixels, got {litPixels}");
    }

    [Fact]
    public void ParticleSystem_RespectsFixedPoolLimitAndUpdatesWithoutOverflow()
    {
        var ps = new ParticleSystem();
        ps.Reset();

        for (int i = 0; i < 200; i++)
        {
            ps.EmitSparks(50, 50, count: 2);
        }

        Assert.Equal(ParticleSystem.MaxParticles, ps.ActiveCount);

        ps.Update(0.1f);
        using var buf = new PixelBuffer(100, 100);
        buf.ClearTransparent();
        ps.Render(buf);

        int lit = 0;
        foreach (int p in buf.ReadOnlyPixels)
        {
            if (p != 0) lit++;
        }
        Assert.True(lit > 0);

        // Advance past max lifetime
        for (int step = 0; step < 20; step++)
        {
            ps.Update(0.1f);
        }
        Assert.Equal(0, ps.ActiveCount);
    }

    [Fact]
    public void BlitNearestNeighborTo_UpscalesExact2x()
    {
        using var src = new PixelBuffer(4, 3);
        using var dst = new PixelBuffer(8, 6);

        int c1 = ColorUtil.FromRgb(12, 34, 56);
        int c2 = ColorUtil.FromRgb(200, 100, 50);
        src.Clear(c1);
        src.SetPixel(2, 1, c2);

        src.BlitNearestNeighborTo(dst, 2);

        Assert.Equal(c2, dst.GetPixel(4, 2));
        Assert.Equal(c2, dst.GetPixel(5, 2));
        Assert.Equal(c2, dst.GetPixel(4, 3));
        Assert.Equal(c2, dst.GetPixel(5, 3));
        Assert.Equal(c1, dst.GetPixel(0, 0));
    }
}
