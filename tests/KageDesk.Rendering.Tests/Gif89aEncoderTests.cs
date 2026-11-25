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
    public void Encode_ExactPixelRgbEquality_AcrossFrames()
    {
        int width = 32;
        int height = 32;
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, width, height, scale: 1);

        int[] f1 = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int red = (x % 8) * 32;
                int green = (y % 8) * 32;
                f1[y * width + x] = unchecked((int)(0xFF000000 | (uint)(red << 16) | (uint)(green << 8) | 0x80));
            }
        }

        int[] f2 = (int[])f1.Clone();
        // Modify a 10x10 patch in frame 2
        for (int y = 5; y < 15; y++)
        {
            for (int x = 5; x < 15; x++)
            {
                f2[y * width + x] = unchecked((int)0xFFFF0055);
            }
        }

        encoder.AddFrame(f1, 8);
        encoder.AddFrame(f2, 8);
        encoder.Finish();

        ms.Position = 0;
        using var img = Image.FromStream(ms);
        var dim = new FrameDimension(img.FrameDimensionsList[0]);
        Assert.Equal(2, img.GetFrameCount(dim));

        using var composite = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(composite);

        // Frame 1 check
        img.SelectActiveFrame(dim, 0);
        g.DrawImage(img, 0, 0);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int expected = f1[y * width + x] & 0x00FFFFFF;
                int actual = composite.GetPixel(x, y).ToArgb() & 0x00FFFFFF;
                Assert.Equal(expected, actual);
            }
        }

        // Frame 2 check
        img.SelectActiveFrame(dim, 1);
        g.DrawImage(img, 0, 0);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int expected = f2[y * width + x] & 0x00FFFFFF;
                int actual = composite.GetPixel(x, y).ToArgb() & 0x00FFFFFF;
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Encode_ComplexFrame_MoreThan512LzwCodes_DecodesExactPixels()
    {
        // 64x64 = 4096 pixels with varying sequence triggering >512 LZW dictionary codes
        int width = 64;
        int height = 64;
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, width, height, scale: 1);

        int[] frame = new int[width * height];
        for (int i = 0; i < frame.Length; i++)
        {
            int colorIndex = (i ^ (i >> 3) ^ (i >> 6)) & 0x0F;
            int r = (colorIndex * 17) & 0xFF;
            int g = (colorIndex * 31) & 0xFF;
            int b = (colorIndex * 53) & 0xFF;
            frame[i] = unchecked((int)(0xFF000000 | (uint)(r << 16) | (uint)(g << 8) | (uint)b));
        }

        encoder.AddFrame(frame, 10);
        encoder.Finish();

        ms.Position = 0;
        using var img = Image.FromStream(ms);
        using var bmp = new Bitmap(img);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int expected = frame[y * width + x] & 0x00FFFFFF;
                int actual = bmp.GetPixel(x, y).ToArgb() & 0x00FFFFFF;
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Encode_LargeFrame_MoreThan4096LzwCodes_TriggersClearCodeResetAndDecodesExactPixels()
    {
        // 128x128 = 16384 pixels with high entropy pattern triggering Clear code table reset (>4096 codes)
        int width = 128;
        int height = 128;
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, width, height, scale: 1);

        int[] frame = new int[width * height];
        for (int i = 0; i < frame.Length; i++)
        {
            int c = ((i * 37) + (i >> 2)) & 0x1F;
            frame[i] = unchecked((int)(0xFF000000 | (uint)(c * 8 << 16) | (uint)(c * 4 << 8) | (uint)(c * 7)));
        }

        encoder.AddFrame(frame, 8);
        encoder.Finish();

        ms.Position = 0;
        using var img = Image.FromStream(ms);
        using var bmp = new Bitmap(img);
        Assert.Equal(width, bmp.Width);
        Assert.Equal(height, bmp.Height);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int expected = frame[y * width + x] & 0x00FFFFFF;
                int actual = bmp.GetPixel(x, y).ToArgb() & 0x00FFFFFF;
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Encode_HighColorFrame2Fallback_OpaqueWithBlackPreserved()
    {
        int width = 20;
        int height = 20; // 400 pixels total
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, width, height, scale: 1);

        // Frame 1:
        // Pixels 0..49: blue (0, 0, 255)
        // Pixels 50..349: gray (128, 128, 128)
        // Pixels 350..399 (known block to be preserved in Frame 2): pure red (255, 0, 0)
        int[] f1 = new int[width * height];
        for (int i = 0; i < f1.Length; i++)
        {
            if (i < 50)
                f1[i] = unchecked((int)0xFF0000FF); // pure blue
            else if (i < 350)
                f1[i] = unchecked((int)0xFF808080); // gray
            else
                f1[i] = unchecked((int)0xFFFF0000); // pure red
        }

        // Frame 2:
        // Pixels 0..49: changed to pure black (0, 0, 0)
        // Pixels 50..349 (300 pixels): changed to 300 genuinely distinct RGB (r, g, b) values
        // where each color remains distinct even under 4-bit masking (forcing RGB332 fallback)
        // Pixels 350..399: left unchanged (pure red)
        int[] f2 = (int[])f1.Clone();
        for (int i = 0; i < 50; i++)
        {
            f2[i] = unchecked((int)0xFF000000); // pure black
        }
        for (int k = 0; k < 300; k++)
        {
            int r4 = (k % 16) * 16;
            int g4 = ((k / 16) % 16) * 16;
            int b4 = ((k / 256) % 16) * 16 + 16;
            f2[50 + k] = unchecked((int)(0xFF000000 | (uint)(r4 << 16) | (uint)(g4 << 8) | (uint)b4));
        }

        encoder.AddFrame(f1, 8);
        encoder.AddFrame(f2, 8);
        encoder.Finish();

        byte[] rawBytes = ms.ToArray();

        // Find the second Graphic Control Extension block (0x21, 0xF9, 0x04) for frame 2
        int gceCount = 0;
        int gce2Offset = -1;
        for (int i = 0; i < rawBytes.Length - 4; i++)
        {
            if (rawBytes[i] == 0x21 && rawBytes[i + 1] == 0xF9 && rawBytes[i + 2] == 0x04)
            {
                gceCount++;
                if (gceCount == 2)
                {
                    gce2Offset = i;
                    break;
                }
            }
        }
        Assert.Equal(2, gceCount);
        Assert.True(gce2Offset >= 0);

        // Inspect encoded GCE packed byte for frame 2: (gcePacked & 0x01) == 0 (opaque, no transparency flag!)
        byte gce2Packed = rawBytes[gce2Offset + 3];
        Assert.Equal(0, gce2Packed & 0x01);

        // Decode BOTH frames via System.Drawing.Image
        ms.Position = 0;
        using var img = Image.FromStream(ms);
        var dim = new FrameDimension(img.FrameDimensionsList[0]);
        Assert.Equal(2, img.GetFrameCount(dim));

        using var composite = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(composite);

        // Frame 1 composite
        img.SelectActiveFrame(dim, 0);
        g.DrawImage(img, 0, 0);

        // Frame 2 composite
        img.SelectActiveFrame(dim, 1);
        g.DrawImage(img, 0, 0);

        // Assert exact black (0, 0, 0) on the changed-to-black block (pixels 0..49)
        for (int i = 0; i < 50; i++)
        {
            int x = i % width;
            int y = i / width;
            Color pixel = composite.GetPixel(x, y);
            Assert.Equal(255, pixel.A);
            Assert.Equal(0, pixel.R);
            Assert.Equal(0, pixel.G);
            Assert.Equal(0, pixel.B);
        }

        // Assert preserved color (pure red: 255, 0, 0) on the unchanged block (pixels 350..399)
        for (int i = 350; i < 400; i++)
        {
            int x = i % width;
            int y = i / width;
            Color pixel = composite.GetPixel(x, y);
            Assert.Equal(255, pixel.A);
            Assert.Equal(255, pixel.R);
            Assert.Equal(0, pixel.G);
            Assert.Equal(0, pixel.B);
        }
    }

    [Fact]
    public void Constructor_PixelCountOverflow_ThrowsArgumentOutOfRangeException_WithoutWritingBytes()
    {
        using var ms = new MemoryStream();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Gif89aEncoder(ms, 1, 1, 46341));
        Assert.Equal(0, ms.Length);
        Assert.Contains("Scaled pixel count", ex.Message);
    }

    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(10, 0, 1)]
    [InlineData(-5, 10, 1)]
    [InlineData(10, -5, 1)]
    [InlineData(10, 10, 0)]
    [InlineData(10, 10, -1)]
    [InlineData(40000, 2, 2)] // 40000 * 2 = 80000 > 65535
    public void Constructor_InvalidDimensionsOrScale_Throws(int width, int height, int scale)
    {
        using var ms = new MemoryStream();
        Assert.ThrowsAny<ArgumentException>(() => new Gif89aEncoder(ms, width, height, scale));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void AddFrame_InvalidDelay_Throws(int delay)
    {
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, 10, 10, 1);
        int[] pixels = new int[100];
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.AddFrame(pixels, delay));
    }

    [Fact]
    public void AddFrame_WrongPixelBufferLength_Throws()
    {
        using var ms = new MemoryStream();
        var encoder = new Gif89aEncoder(ms, 10, 10, 1);
        int[] wrongLength = new int[50];
        Assert.Throws<ArgumentException>(() => encoder.AddFrame(wrongLength, 8));
    }
}
