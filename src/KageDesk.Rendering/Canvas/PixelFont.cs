namespace KageDesk.Rendering.Canvas;

public enum PixelFontSize
{
    Small,   // 3x5 glyph cell, 4px advance
    Regular  // 5x7 glyph cell, 6px advance
}

public static class PixelFont
{
    // 3x5 bitmap glyphs packed into 15 bits (3 bits per row, MSB = left pixel, row 0 = bits 14..12)
    private static readonly Dictionary<char, ushort> Glyphs3x5 = new()
    {
        ['A'] = Pack3x5(0b010, 0b101, 0b111, 0b101, 0b101),
        ['B'] = Pack3x5(0b110, 0b101, 0b110, 0b101, 0b110),
        ['C'] = Pack3x5(0b011, 0b100, 0b100, 0b100, 0b011),
        ['D'] = Pack3x5(0b110, 0b101, 0b101, 0b101, 0b110),
        ['E'] = Pack3x5(0b111, 0b100, 0b110, 0b100, 0b111),
        ['F'] = Pack3x5(0b111, 0b100, 0b110, 0b100, 0b100),
        ['G'] = Pack3x5(0b011, 0b100, 0b101, 0b101, 0b011),
        ['H'] = Pack3x5(0b101, 0b101, 0b111, 0b101, 0b101),
        ['I'] = Pack3x5(0b111, 0b010, 0b010, 0b010, 0b111),
        ['J'] = Pack3x5(0b001, 0b001, 0b001, 0b101, 0b010),
        ['K'] = Pack3x5(0b101, 0b110, 0b100, 0b110, 0b101),
        ['L'] = Pack3x5(0b100, 0b100, 0b100, 0b100, 0b111),
        ['M'] = Pack3x5(0b101, 0b111, 0b101, 0b101, 0b101),
        ['N'] = Pack3x5(0b110, 0b101, 0b101, 0b101, 0b101),
        ['O'] = Pack3x5(0b010, 0b101, 0b101, 0b101, 0b010),
        ['P'] = Pack3x5(0b110, 0b101, 0b110, 0b100, 0b100),
        ['Q'] = Pack3x5(0b010, 0b101, 0b101, 0b111, 0b011),
        ['R'] = Pack3x5(0b110, 0b101, 0b110, 0b101, 0b101),
        ['S'] = Pack3x5(0b011, 0b100, 0b010, 0b001, 0b110),
        ['T'] = Pack3x5(0b111, 0b010, 0b010, 0b010, 0b010),
        ['U'] = Pack3x5(0b101, 0b101, 0b101, 0b101, 0b111),
        ['V'] = Pack3x5(0b101, 0b101, 0b101, 0b101, 0b010),
        ['W'] = Pack3x5(0b101, 0b101, 0b101, 0b111, 0b101),
        ['X'] = Pack3x5(0b101, 0b101, 0b010, 0b101, 0b101),
        ['Y'] = Pack3x5(0b101, 0b101, 0b010, 0b010, 0b010),
        ['Z'] = Pack3x5(0b111, 0b001, 0b010, 0b100, 0b111),

        ['0'] = Pack3x5(0b111, 0b101, 0b101, 0b101, 0b111),
        ['1'] = Pack3x5(0b010, 0b110, 0b010, 0b010, 0b111),
        ['2'] = Pack3x5(0b110, 0b001, 0b010, 0b100, 0b111),
        ['3'] = Pack3x5(0b110, 0b001, 0b010, 0b001, 0b110),
        ['4'] = Pack3x5(0b101, 0b101, 0b111, 0b001, 0b001),
        ['5'] = Pack3x5(0b111, 0b100, 0b110, 0b001, 0b110),
        ['6'] = Pack3x5(0b011, 0b100, 0b111, 0b101, 0b111),
        ['7'] = Pack3x5(0b111, 0b001, 0b010, 0b010, 0b010),
        ['8'] = Pack3x5(0b111, 0b101, 0b111, 0b101, 0b111),
        ['9'] = Pack3x5(0b111, 0b101, 0b111, 0b001, 0b110),

        [' '] = 0,
        [':'] = Pack3x5(0b000, 0b010, 0b000, 0b010, 0b000),
        ['.'] = Pack3x5(0b000, 0b000, 0b000, 0b000, 0b010),
        [','] = Pack3x5(0b000, 0b000, 0b000, 0b010, 0b100),
        ['/'] = Pack3x5(0b001, 0b001, 0b010, 0b100, 0b100),
        ['!'] = Pack3x5(0b010, 0b010, 0b010, 0b000, 0b010),
        ['?'] = Pack3x5(0b110, 0b001, 0b010, 0b000, 0b010),
        ['-'] = Pack3x5(0b000, 0b000, 0b111, 0b000, 0b000),
        ['_'] = Pack3x5(0b000, 0b000, 0b000, 0b000, 0b111),
        ['['] = Pack3x5(0b110, 0b100, 0b100, 0b100, 0b110),
        [']'] = Pack3x5(0b011, 0b001, 0b001, 0b001, 0b011),
        ['('] = Pack3x5(0b010, 0b100, 0b100, 0b100, 0b010),
        [')'] = Pack3x5(0b010, 0b001, 0b001, 0b001, 0b010),
        ['·'] = Pack3x5(0b000, 0b000, 0b010, 0b000, 0b000),
        ['•'] = Pack3x5(0b000, 0b000, 0b010, 0b000, 0b000),
        ['%'] = Pack3x5(0b101, 0b001, 0b010, 0b100, 0b101),
        ['+'] = Pack3x5(0b000, 0b010, 0b111, 0b010, 0b000),
        ['#'] = Pack3x5(0b101, 0b111, 0b101, 0b111, 0b101),
        ['@'] = Pack3x5(0b111, 0b101, 0b111, 0b100, 0b011),
        ['\''] = Pack3x5(0b010, 0b010, 0b000, 0b000, 0b000),
        ['"'] = Pack3x5(0b101, 0b101, 0b000, 0b000, 0b000),
        ['*'] = Pack3x5(0b101, 0b010, 0b101, 0b000, 0b000),
        ['='] = Pack3x5(0b000, 0b111, 0b000, 0b111, 0b000),
        ['>'] = Pack3x5(0b100, 0b010, 0b001, 0b010, 0b100),
        ['<'] = Pack3x5(0b001, 0b010, 0b100, 0b010, 0b001),
        ['|'] = Pack3x5(0b010, 0b010, 0b010, 0b010, 0b010)
    };

    // 5x7 bitmap glyphs: 7 rows, 5 bits per row (bit 4 = leftmost pixel)
    private static readonly Dictionary<char, byte[]> Glyphs5x7 = new()
    {
        ['A'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['B'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 },
        ['C'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 },
        ['D'] = new byte[] { 0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100 },
        ['E'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 },
        ['F'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['G'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110 },
        ['H'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['I'] = new byte[] { 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['J'] = new byte[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b10010, 0b10010, 0b01100 },
        ['K'] = new byte[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001 },
        ['L'] = new byte[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 },
        ['M'] = new byte[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 },
        ['N'] = new byte[] { 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001 },
        ['O'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['P'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['Q'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101 },
        ['R'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 },
        ['S'] = new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['T'] = new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['U'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['V'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100 },
        ['W'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001 },
        ['X'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 },
        ['Y'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['Z'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111 },

        ['0'] = new byte[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 },
        ['1'] = new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['2'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111 },
        ['3'] = new byte[] { 0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['4'] = new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 },
        ['5'] = new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 },
        ['6'] = new byte[] { 0b01110, 0b10000, 0b11110, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['7'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 },
        ['8'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 },
        ['9'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110 },

        [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
        [':'] = new byte[] { 0b00000, 0b00100, 0b00100, 0b00000, 0b00100, 0b00100, 0b00000 },
        ['.'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00110, 0b00110 },
        [','] = new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00100, 0b00100, 0b01000 },
        ['/'] = new byte[] { 0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000 },
        ['!'] = new byte[] { 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100 },
        ['?'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00110, 0b00100, 0b00000, 0b00100 },
        ['-'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000 },
        ['_'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111 },
        ['['] = new byte[] { 0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110 },
        [']'] = new byte[] { 0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110 },
        ['('] = new byte[] { 0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010 },
        [')'] = new byte[] { 0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000 },
        ['·'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b00100, 0b00000, 0b00000, 0b00000 },
        ['•'] = new byte[] { 0b00000, 0b00000, 0b00110, 0b00110, 0b00000, 0b00000, 0b00000 },
        ['%'] = new byte[] { 0b11001, 0b11010, 0b00010, 0b00100, 0b01000, 0b01011, 0b10011 },
        ['+'] = new byte[] { 0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000 },
        ['#'] = new byte[] { 0b01010, 0b11111, 0b01010, 0b01010, 0b11111, 0b01010, 0b00000 },
        ['@'] = new byte[] { 0b01110, 0b10001, 0b10111, 0b10101, 0b10111, 0b10000, 0b01110 },
        ['\''] = new byte[] { 0b00100, 0b00100, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000 },
        ['"'] = new byte[] { 0b01010, 0b01010, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000 },
        ['*'] = new byte[] { 0b00000, 0b10101, 0b01110, 0b11111, 0b01110, 0b10101, 0b00000 },
        ['='] = new byte[] { 0b00000, 0b00000, 0b11111, 0b00000, 0b11111, 0b00000, 0b00000 },
        ['>'] = new byte[] { 0b10000, 0b01000, 0b00100, 0b00010, 0b00100, 0b01000, 0b10000 },
        ['<'] = new byte[] { 0b00001, 0b00010, 0b00100, 0b01000, 0b00100, 0b00010, 0b00001 },
        ['|'] = new byte[] { 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 }
    };

    private static ushort Pack3x5(int r0, int r1, int r2, int r3, int r4)
        => (ushort)((r0 << 12) | (r1 << 9) | (r2 << 6) | (r3 << 3) | r4);

    public static int CharWidth(PixelFontSize size) => size == PixelFontSize.Small ? 3 : 5;
    public static int CharAdvance(PixelFontSize size) => size == PixelFontSize.Small ? 4 : 6;
    public static int LineHeight(PixelFontSize size) => size == PixelFontSize.Small ? 5 : 7;

    public static int MeasureWidth(ReadOnlySpan<char> text, PixelFontSize size = PixelFontSize.Small)
    {
        if (text.Length == 0) return 0;
        return text.Length * CharAdvance(size) - 1;
    }

    public static void DrawText(
        PixelBuffer buf,
        int x,
        int y,
        ReadOnlySpan<char> text,
        int color,
        PixelFontSize size = PixelFontSize.Small,
        int shadowColor = 0)
    {
        if (text.Length == 0) return;
        int advance = CharAdvance(size);
        int cursorX = x;

        for (int i = 0; i < text.Length; i++)
        {
            char c = char.ToUpperInvariant(text[i]);
            if (size == PixelFontSize.Small)
            {
                if (Glyphs3x5.TryGetValue(c, out ushort bits) && bits != 0)
                {
                    if (shadowColor != 0) DrawGlyph3x5(buf, cursorX + 1, y + 1, bits, shadowColor);
                    DrawGlyph3x5(buf, cursorX, y, bits, color);
                }
            }
            else
            {
                if (Glyphs5x7.TryGetValue(c, out byte[]? rows))
                {
                    if (shadowColor != 0) DrawGlyph5x7(buf, cursorX + 1, y + 1, rows, shadowColor);
                    DrawGlyph5x7(buf, cursorX, y, rows, color);
                }
            }

            cursorX += advance;
        }
    }

    public static void DrawTextCentered(
        PixelBuffer buf,
        int centerX,
        int y,
        ReadOnlySpan<char> text,
        int color,
        PixelFontSize size = PixelFontSize.Small,
        int shadowColor = 0)
    {
        int w = MeasureWidth(text, size);
        DrawText(buf, centerX - w / 2, y, text, color, size, shadowColor);
    }

    public static void DrawTextTruncated(
        PixelBuffer buf,
        int x,
        int y,
        int maxWidthPx,
        ReadOnlySpan<char> text,
        int color,
        PixelFontSize size = PixelFontSize.Small,
        int shadowColor = 0)
    {
        if (text.Length == 0 || maxWidthPx <= 0) return;
        int advance = CharAdvance(size);
        int maxChars = (maxWidthPx + 1) / advance;
        if (maxChars <= 0) return;

        if (text.Length <= maxChars)
        {
            DrawText(buf, x, y, text, color, size, shadowColor);
            return;
        }

        if (maxChars <= 2)
        {
            DrawText(buf, x, y, text.Slice(0, maxChars), color, size, shadowColor);
            return;
        }

        Span<char> scratch = maxChars <= 128 ? stackalloc char[maxChars] : new char[maxChars];
        text.Slice(0, maxChars - 2).CopyTo(scratch);
        scratch[maxChars - 2] = '.';
        scratch[maxChars - 1] = '.';
        DrawText(buf, x, y, scratch, color, size, shadowColor);
    }

    public static void DrawWrappedText(
        PixelBuffer buf,
        int x,
        int y,
        int maxWidthPx,
        int maxLines,
        ReadOnlySpan<char> text,
        int color,
        PixelFontSize size = PixelFontSize.Small,
        int lineSpacing = 2,
        int shadowColor = 0)
    {
        if (text.Length == 0 || maxLines <= 0 || maxWidthPx <= 0) return;

        int advance = CharAdvance(size);
        int maxCharsPerLine = (maxWidthPx + 1) / advance;
        if (maxCharsPerLine <= 0) return;

        int lineStep = LineHeight(size) + lineSpacing;
        int start = 0;
        int len = text.Length;

        for (int line = 0; line < maxLines && start < len; line++)
        {
            while (start < len && text[start] == ' ') start++;
            if (start >= len) break;

            int remaining = len - start;
            bool isLastLine = line == maxLines - 1;

            if (remaining <= maxCharsPerLine)
            {
                DrawText(buf, x, y + line * lineStep, text.Slice(start, remaining), color, size, shadowColor);
                break;
            }

            if (isLastLine)
            {
                DrawTextTruncated(buf, x, y + line * lineStep, maxWidthPx, text.Slice(start), color, size, shadowColor);
                break;
            }

            // Try to break on a space within maxCharsPerLine
            int breakIdx = -1;
            int searchEnd = start + maxCharsPerLine;
            for (int i = searchEnd; i > start; i--)
            {
                if (text[i] == ' ')
                {
                    breakIdx = i;
                    break;
                }
            }

            int take = breakIdx > start ? breakIdx - start : maxCharsPerLine;
            DrawText(buf, x, y + line * lineStep, text.Slice(start, take), color, size, shadowColor);
            start += take;
        }
    }

    private static void DrawGlyph3x5(PixelBuffer buf, int x, int y, ushort bits, int color)
    {
        for (int row = 0; row < 5; row++)
        {
            int rowBits = (bits >> ((4 - row) * 3)) & 0b111;
            if ((rowBits & 0b100) != 0) buf.SetPixel(x, y + row, color);
            if ((rowBits & 0b010) != 0) buf.SetPixel(x + 1, y + row, color);
            if ((rowBits & 0b001) != 0) buf.SetPixel(x + 2, y + row, color);
        }
    }

    private static void DrawGlyph5x7(PixelBuffer buf, int x, int y, byte[] rows, int color)
    {
        for (int row = 0; row < 7; row++)
        {
            byte rowBits = rows[row];
            if (rowBits == 0) continue;
            if ((rowBits & 0b10000) != 0) buf.SetPixel(x, y + row, color);
            if ((rowBits & 0b01000) != 0) buf.SetPixel(x + 1, y + row, color);
            if ((rowBits & 0b00100) != 0) buf.SetPixel(x + 2, y + row, color);
            if ((rowBits & 0b00010) != 0) buf.SetPixel(x + 3, y + row, color);
            if ((rowBits & 0b00001) != 0) buf.SetPixel(x + 4, y + row, color);
        }
    }
}
