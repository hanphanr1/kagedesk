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

    [Fact]
    public void PixelFont_DrawWrappedText_ApprovalCard_RetainsBoundaryWordOnLine1_Ascii()
    {
        const int maxWidthPx = 192;
        const int maxLines = 2;
        const PixelFontSize size = PixelFontSize.Small;
        const int lineSpacing = 4;
        int lineStep = PixelFont.LineHeight(size) + lineSpacing; // 5 + 4 = 9
        int color = ColorUtil.White;

        string line1 = "This is a test of the emergency broadcast system";
        string line2 = "Please respond immediately to approve this task.";
        Assert.Equal(48, line1.Length);
        Assert.Equal(48, line2.Length);

        string prompt = line1 + " " + line2;

        int bufferWidth = 200;
        int bufferHeight = lineStep * maxLines + 4;

        using var bufWrapped = new PixelBuffer(bufferWidth, bufferHeight);
        using var bufExpected = new PixelBuffer(bufferWidth, bufferHeight);

        // Render with DrawWrappedText
        PixelFont.DrawWrappedText(bufWrapped, 0, 0, maxWidthPx, maxLines, prompt, color, size, lineSpacing);

        // Render known 2-line layout directly with DrawText
        PixelFont.DrawText(bufExpected, 0, 0, line1, color, size);
        PixelFont.DrawText(bufExpected, 0, lineStep, line2, color, size);

        // Pixel-by-pixel equality: wrapped text must exactly equal expected 2-line layout
        var wrappedPixels = bufWrapped.ReadOnlyPixels;
        var expectedPixels = bufExpected.ReadOnlyPixels;
        Assert.Equal(expectedPixels.Length, wrappedPixels.Length);

        for (int i = 0; i < expectedPixels.Length; i++)
        {
            Assert.Equal(expectedPixels[i], wrappedPixels[i]);
        }

        // Concretely verify Line 1 retains its final word ("system")
        // "system" starts at char index 42 on line 1: x in [42 * 4, 48 * 4 - 1] = [168, 191]
        int line1FinalWordLitPixels = CountLitPixels(bufWrapped, 42 * 4, 0, 6 * 4, PixelFont.LineHeight(size));
        Assert.True(line1FinalWordLitPixels > 0, "Line 1 must retain 'system' (final word was dropped or wrapped onto line 2!)");

        // Concretely verify Line 2 renders its full expected text and is not clipped
        // "task." ends at char index 47 on line 2 (period at index 47: x = 47 * 4 = 188)
        int line2EndLitPixels = CountLitPixels(bufWrapped, 47 * 4, lineStep, 4, PixelFont.LineHeight(size));
        Assert.True(line2EndLitPixels > 0, "Line 2 must retain its final character (was clipped!)");
    }

    [Theory]
    [InlineData(false)] // Single combining mark on final char (FormD 'ú' = 'u' + \u0301)
    [InlineData(true)]  // Dual combining marks on final char (FormD 'ộ' = 'o' + \u0302 + \u0323)
    public void PixelFont_DrawWrappedText_ApprovalCard_RetainsBoundaryWordOnLine1_Vietnamese_BothNfcAndNfd(bool dualCombiningMark)
    {
        const int maxWidthPx = 192;
        const int maxLines = 2;
        const PixelFontSize size = PixelFontSize.Small;
        const int lineSpacing = 4;
        int lineStep = PixelFont.LineHeight(size) + lineSpacing; // 9
        int color = ColorUtil.White;

        string line1Base = "Yêu cầu phê duyệt hành động từ quản trị viên chú";
        string line1Nfc = dualCombiningMark
            ? line1Base[..^1] + "ộ"
            : line1Base;
        string line2Nfc = "Vui lòng nhấn nút Duyệt để tiếp tục thực hiện.";

        string line1FoldedAscii = dualCombiningMark
            ? "YEU CAU PHE DUYET HANH DONG TU QUAN TRI VIEN CHO"
            : "YEU CAU PHE DUYET HANH DONG TU QUAN TRI VIEN CHU";
        string line2FoldedAscii = "VUI LONG NHAN NUT DUYET DE TIEP TUC THUC HIEN.";

        string line1Nfd = line1Nfc.Normalize(NormalizationForm.FormD);
        string line2Nfd = line2Nfc.Normalize(NormalizationForm.FormD);

        string promptNfc = line1Nfc + " " + line2Nfc;
        string promptNfd = line1Nfd + " " + line2Nfd;
        string promptAscii = line1FoldedAscii + " " + line2FoldedAscii;

        int bufferWidth = 200;
        int bufferHeight = lineStep * maxLines + 4;

        using var bufWrappedNfd = new PixelBuffer(bufferWidth, bufferHeight);
        using var bufWrappedNfc = new PixelBuffer(bufferWidth, bufferHeight);
        using var bufWrappedAscii = new PixelBuffer(bufferWidth, bufferHeight);
        using var bufExpected = new PixelBuffer(bufferWidth, bufferHeight);

        PixelFont.DrawWrappedText(bufWrappedNfd, 0, 0, maxWidthPx, maxLines, promptNfd, color, size, lineSpacing);
        PixelFont.DrawWrappedText(bufWrappedNfc, 0, 0, maxWidthPx, maxLines, promptNfc, color, size, lineSpacing);
        PixelFont.DrawWrappedText(bufWrappedAscii, 0, 0, maxWidthPx, maxLines, promptAscii, color, size, lineSpacing);

        // Expected 2-line rendering directly drawn
        PixelFont.DrawText(bufExpected, 0, 0, line1FoldedAscii, color, size);
        PixelFont.DrawText(bufExpected, 0, lineStep, line2FoldedAscii, color, size);

        // Assert NFD, NFC, and ASCII wrapped renderings match expected pixel-for-pixel
        var exp = bufExpected.ReadOnlyPixels;
        var nfd = bufWrappedNfd.ReadOnlyPixels;
        var nfc = bufWrappedNfc.ReadOnlyPixels;
        var asc = bufWrappedAscii.ReadOnlyPixels;

        for (int i = 0; i < exp.Length; i++)
        {
            Assert.Equal(exp[i], asc[i]);
            Assert.Equal(exp[i], nfc[i]);
            Assert.Equal(exp[i], nfd[i]);
        }

        // Concretely verify Line 1 retains final word (starts at char 45: 3 chars * 4 = 12 px)
        int line1FinalWordLit = CountLitPixels(bufWrappedNfd, 45 * 4, 0, 3 * 4, PixelFont.LineHeight(size));
        Assert.True(line1FinalWordLit > 0, "Line 1 must retain final word with trailing NFD combining mark!");

        // Concretely verify Line 2 starts with full expected word ("VUI") and is not corrupted by orphaned combining mark
        int line2StartLit = CountLitPixels(bufWrappedNfd, 0, lineStep, 3 * 4, PixelFont.LineHeight(size));
        Assert.True(line2StartLit > 0, "Line 2 must render 'Vui' cleanly!");

        // Concretely verify Line 2 renders its full text and is not clipped (period at index 45: x = 45 * 4 = 180)
        int line2EndLit = CountLitPixels(bufWrappedNfd, 45 * 4, lineStep, 4, PixelFont.LineHeight(size));
        Assert.True(line2EndLit > 0, "Line 2 must render trailing punctuation and not be clipped!");
    }

    [Fact]
    public void PixelFont_DrawWrappedText_MidWordHardBreak_WithNfdCombiningMarks_KeepsMarkAttached()
    {
        const int maxWidthPx = 192; // 48 chars
        const int maxLines = 2;
        const PixelFontSize size = PixelFontSize.Small;
        const int lineSpacing = 4;
        int lineStep = PixelFont.LineHeight(size) + lineSpacing;
        int color = ColorUtil.White;

        // 47 ASCII chars + 1 accented char (FormD 'ộ' = 'o' + \u0302 + \u0323) + 4 ASCII chars (no space, mid-word)
        string basePrefix = new string('A', 47);
        string accentedCharNfd = "ộ".Normalize(NormalizationForm.FormD);
        string suffix = "BBBB";
        string longWord = basePrefix + accentedCharNfd + suffix; // 48 visible chars on line 1, 4 on line 2

        using var bufWrapped = new PixelBuffer(200, lineStep * maxLines + 4);
        using var bufExpected = new PixelBuffer(200, lineStep * maxLines + 4);

        PixelFont.DrawWrappedText(bufWrapped, 0, 0, maxWidthPx, maxLines, longWord, color, size, lineSpacing);

        // Expected: Line 1 gets 47 'A's + 'O' (folded from 'ộ'), Line 2 gets "BBBB"
        string expectedLine1 = new string('A', 47) + "O";
        string expectedLine2 = suffix;
        PixelFont.DrawText(bufExpected, 0, 0, expectedLine1, color, size);
        PixelFont.DrawText(bufExpected, 0, lineStep, expectedLine2, color, size);

        var wrappedPixels = bufWrapped.ReadOnlyPixels;
        var expectedPixels = bufExpected.ReadOnlyPixels;
        for (int i = 0; i < expectedPixels.Length; i++)
        {
            Assert.Equal(expectedPixels[i], wrappedPixels[i]);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void PixelFont_DrawWrappedText_ExplicitNewlines_BreaksAcrossLines(string newline)
    {
        const int maxWidthPx = 192;
        const int maxLines = 2;
        const PixelFontSize size = PixelFontSize.Small;
        const int lineSpacing = 4;
        int lineStep = PixelFont.LineHeight(size) + lineSpacing;
        int color = ColorUtil.White;

        string line1 = "Approval requested:";
        string line2 = "Deploy to production environment";
        string prompt = line1 + newline + line2;

        using var bufWrapped = new PixelBuffer(200, lineStep * maxLines + 4);
        using var bufExpected = new PixelBuffer(200, lineStep * maxLines + 4);

        PixelFont.DrawWrappedText(bufWrapped, 0, 0, maxWidthPx, maxLines, prompt, color, size, lineSpacing);

        PixelFont.DrawText(bufExpected, 0, 0, line1, color, size);
        PixelFont.DrawText(bufExpected, 0, lineStep, line2, color, size);

        var wrappedPixels = bufWrapped.ReadOnlyPixels;
        var expectedPixels = bufExpected.ReadOnlyPixels;
        for (int i = 0; i < expectedPixels.Length; i++)
        {
            Assert.Equal(expectedPixels[i], wrappedPixels[i]);
        }
    }

    private static int CountLitPixels(PixelBuffer buf, int startX, int startY, int width, int height)
    {
        int count = 0;
        for (int x = startX; x < startX + width; x++)
        {
            for (int y = startY; y < startY + height; y++)
            {
                if (buf.GetPixel(x, y) != 0) count++;
            }
        }
        return count;
    }
}
