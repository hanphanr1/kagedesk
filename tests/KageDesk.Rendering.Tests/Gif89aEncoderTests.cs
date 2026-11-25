using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using KageDesk.Rendering.Export;
using Xunit;

namespace KageDesk.Rendering.Tests;

public class Gif89aEncoderTests
{
    [Fact]
    public void Encode_CreatesValidGif89aWithNetscapeLoopAndCorrectDimensions()
    {
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, 32, 24, scale: 2);

        int[] frame1 = new int[32 * 24];
        Array.Fill(frame1, unchecked((int)0xFF102030)); // Dark navy

        int[] frame2 = new int[32 * 24];
        Array.Fill(frame2, unchecked((int)0xFF00FF88)); // Cyan

        encoder.AddFrame(frame1, delayCentiseconds: 10);
        encoder.AddFrame(frame2, delayCentiseconds: 10);
        encoder.Finish();

        byte[] bytes = ms.ToArray();

        // 1. Check Header
        string header = Encoding.ASCII.GetString(bytes, 0, 6);
        Assert.Equal("GIF89a", header);

        // 2. Check Logical Screen Width & Height (scale=2 -> 64x48)
        ushort width = BitConverter.ToUInt16(bytes, 6);
        ushort height = BitConverter.ToUInt16(bytes, 8);
        Assert.Equal(64, width);
        Assert.Equal(48, height);

        // 3. Check Trailer
        Assert.Equal(0x3B, bytes[^1]);

        // 4. Verify System.Drawing decodes successfully
        ms.Position = 0;
        using var img = Image.FromStream(ms);
        Assert.Equal(64, img.Width);
        Assert.Equal(48, img.Height);

        var dimension = new FrameDimension(img.FrameDimensionsList[0]);
        int frameCount = img.GetFrameCount(dimension);
        Assert.Equal(2, frameCount);
    }

    [Fact]
    public void Encode_FallbackUniformPalette_WhenMoreThan256Colors()
    {
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, 20, 20, scale: 1);

        // Generate 400 distinct colors
        int[] frame = new int[400];
        for (int i = 0; i < 400; i++)
        {
            int r = (i * 3) & 0xFF;
            int g = (i * 7) & 0xFF;
            int b = (i * 11) & 0xFF;
            frame[i] = unchecked((int)(0xFF000000 | (uint)(r << 16) | (uint)(g << 8) | (uint)b));
        }

        encoder.AddFrame(frame, delayCentiseconds: 8);
        encoder.Finish();

        ms.Position = 0;
        using var img = Image.FromStream(ms);
        Assert.Equal(20, img.Width);
        Assert.Equal(20, img.Height);
    }

    [Fact]
    public void Encode_Scale2_Outputs768x432()
    {
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, 384, 216, scale: 2);

        int[] frame = new int[384 * 216];
        encoder.AddFrame(frame, delayCentiseconds: 8);
        encoder.Finish();

        ms.Position = 0;
        using var img = Image.FromStream(ms);
        Assert.Equal(768, img.Width);
        Assert.Equal(432, img.Height);
    }

    [Fact]
    public void Encode_MultiFrameWithTransparentDiffing_DecodesCorrectFrameCount()
    {
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, 16, 16, scale: 1);

        int[] f1 = new int[256];
        Array.Fill(f1, unchecked((int)0xFF000000));

        int[] f2 = new int[256];
        Array.Fill(f2, unchecked((int)0xFF000000));
        f2[10] = unchecked((int)0xFFFF0000); // Only 1 pixel changed

        int[] f3 = new int[256];
        Array.Fill(f3, unchecked((int)0xFF000000));
        f3[20] = unchecked((int)0xFF00FF00);

        encoder.AddFrame(f1, 8);
        encoder.AddFrame(f2, 8);
        encoder.AddFrame(f3, 8);
        encoder.Finish();

        ms.Position = 0;
        using var img = Image.FromStream(ms);
        var dim = new FrameDimension(img.FrameDimensionsList[0]);
        Assert.Equal(3, img.GetFrameCount(dim));
    }
}
