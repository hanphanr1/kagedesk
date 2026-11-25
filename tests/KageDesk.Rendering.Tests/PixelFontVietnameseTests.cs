using System.Text;
using KageDesk.Rendering.Canvas;
using Xunit;

namespace KageDesk.Rendering.Tests;

public class PixelFontVietnameseTests
{
    [Theory]
    [InlineData("mày xem tiếng việt nè", "MAY XEM TIENG VIET NE")]
    [InlineData("Duyệt PR Đã Xong", "DUYET PR DA XONG")]
    public void PixelFont_Vietnamese_MeasuresMatchingAscii_ForBothNfcAndNfd(string original, string foldedAscii)
    {
        string nfc = original.Normalize(NormalizationForm.FormC);
        string nfd = original.Normalize(NormalizationForm.FormD);

        // Small size
        int expectedSmall = PixelFont.MeasureWidth(foldedAscii, PixelFontSize.Small);
        Assert.Equal(expectedSmall, PixelFont.MeasureWidth(nfc, PixelFontSize.Small));
        Assert.Equal(expectedSmall, PixelFont.MeasureWidth(nfd, PixelFontSize.Small));

        // Regular size
        int expectedRegular = PixelFont.MeasureWidth(foldedAscii, PixelFontSize.Regular);
        Assert.Equal(expectedRegular, PixelFont.MeasureWidth(nfc, PixelFontSize.Regular));
        Assert.Equal(expectedRegular, PixelFont.MeasureWidth(nfd, PixelFontSize.Regular));
    }

    [Theory]
    [InlineData("mày xem tiếng việt nè", "MAY XEM TIENG VIET NE", PixelFontSize.Small)]
    [InlineData("mày xem tiếng việt nè", "MAY XEM TIENG VIET NE", PixelFontSize.Regular)]
    [InlineData("Duyệt PR Đã Xong", "DUYET PR DA XONG", PixelFontSize.Small)]
    [InlineData("Duyệt PR Đã Xong", "DUYET PR DA XONG", PixelFontSize.Regular)]
    public void PixelFont_Vietnamese_RendersCleanlyWithZeroMissingLetters(string original, string foldedAscii, PixelFontSize size)
    {
        string nfc = original.Normalize(NormalizationForm.FormC);
        string nfd = original.Normalize(NormalizationForm.FormD);

        int advance = PixelFont.CharAdvance(size);
        int charWidth = PixelFont.CharWidth(size);
        int width = foldedAscii.Length * advance + 10;
        int height = PixelFont.LineHeight(size) + 6;

        using var bufAscii = new PixelBuffer(width, height);
        using var bufNfc = new PixelBuffer(width, height);
        using var bufNfd = new PixelBuffer(width, height);

        int color = ColorUtil.White;
        PixelFont.DrawText(bufAscii, 2, 2, foldedAscii, color, size);
        PixelFont.DrawText(bufNfc, 2, 2, nfc, color, size);
        PixelFont.DrawText(bufNfd, 2, 2, nfd, color, size);

        // NFC and NFD buffers must match ASCII folded rendering pixel-for-pixel
        var asciiPixels = bufAscii.ReadOnlyPixels;
        var nfcPixels = bufNfc.ReadOnlyPixels;
        var nfdPixels = bufNfd.ReadOnlyPixels;

        Assert.Equal(asciiPixels.Length, nfcPixels.Length);
        Assert.Equal(asciiPixels.Length, nfdPixels.Length);

        for (int i = 0; i < asciiPixels.Length; i++)
        {
            Assert.Equal(asciiPixels[i], nfcPixels[i]);
            Assert.Equal(asciiPixels[i], nfdPixels[i]);
        }

        // Verify that every non-space character rendered non-blank pixels (zero missing letters)
        for (int charIdx = 0; charIdx < foldedAscii.Length; charIdx++)
        {
            if (foldedAscii[charIdx] == ' ') continue;

            int charStartX = 2 + charIdx * advance;
            int litPixelsForChar = 0;

            for (int dx = 0; dx < charWidth; dx++)
            {
                for (int dy = 0; dy < PixelFont.LineHeight(size); dy++)
                {
                    if (bufNfc.GetPixel(charStartX + dx, 2 + dy) != 0)
                    {
                        litPixelsForChar++;
                    }
                }
            }

            Assert.True(litPixelsForChar > 0,
                $"Letter '{foldedAscii[charIdx]}' at index {charIdx} had 0 lit pixels (dropped/missing letter!)");
        }
    }
}
