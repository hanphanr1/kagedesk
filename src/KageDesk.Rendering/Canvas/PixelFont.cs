using System.Globalization;

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

    private static char NormalizeGlyphChar(char c)
    {
        if (c <= 0x7F)
        {
            return c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
        }

        return c switch
        {
            // đ / Đ -> 'D'
            'đ' or 'Đ' => 'D',

            // áàảãạăắằẳẵặâấầẩẫậ / ÁÀẢÃẠĂẮẰẲẴẶÂẤẦẨẪẬ + common Latin -> 'A'
            'á' or 'à' or 'ả' or 'ã' or 'ạ' or
            'ă' or 'ắ' or 'ằ' or 'ẳ' or 'ẵ' or 'ặ' or
            'â' or 'ấ' or 'ầ' or 'ẩ' or 'ẫ' or 'ậ' or
            'Á' or 'À' or 'Ả' or 'Ã' or 'Ạ' or
            'Ă' or 'Ắ' or 'Ằ' or 'Ẳ' or 'Ẵ' or 'Ặ' or
            'Â' or 'Ấ' or 'Ầ' or 'Ẩ' or 'Ẫ' or 'Ậ' or
            'ä' or 'Ä' or 'å' or 'Å' or 'ā' or 'Ā' or 'ą' or 'Ą' => 'A',

            // éèẻẽẹêếềểễệ / ÉÈẺẼẸÊẾỀỂỄỆ + common Latin -> 'E'
            'é' or 'è' or 'ẻ' or 'ẽ' or 'ẹ' or
            'ê' or 'ế' or 'ề' or 'ể' or 'ễ' or 'ệ' or
            'É' or 'È' or 'Ẻ' or 'Ẽ' or 'Ẹ' or
            'Ê' or 'Ế' or 'Ề' or 'Ể' or 'Ễ' or 'Ệ' or
            'ë' or 'Ë' or 'ē' or 'Ē' or 'ę' or 'Ę' => 'E',

            // íìỉĩị / ÍÌỈĨỊ + common Latin -> 'I'
            'í' or 'ì' or 'ỉ' or 'ĩ' or 'ị' or
            'Í' or 'Ì' or 'Ỉ' or 'Ĩ' or 'Ị' or
            'î' or 'Î' or 'ï' or 'Ï' or 'ī' or 'Ī' => 'I',

            // óòỏõọôốồổỗộơớờởỡợ / ÓÒỎÕỌÔỐỒỔỖỘƠỚỜỞỠỢ + common Latin -> 'O'
            'ó' or 'ò' or 'ỏ' or 'õ' or 'ọ' or
            'ô' or 'ố' or 'ồ' or 'ổ' or 'ỗ' or 'ộ' or
            'ơ' or 'ớ' or 'ờ' or 'ở' or 'ỡ' or 'ợ' or
            'Ó' or 'Ò' or 'Ỏ' or 'Õ' or 'Ọ' or
            'Ô' or 'Ố' or 'Ồ' or 'Ổ' or 'Ỗ' or 'Ộ' or
            'Ơ' or 'Ớ' or 'Ờ' or 'Ở' or 'Ỡ' or 'Ợ' or
            'ö' or 'Ö' or 'ø' or 'Ø' or 'ō' or 'Ō' => 'O',

            // úùủũụưứừửữự / ÚÙỦŨỤƯỨỪỬỮỰ + common Latin -> 'U'
            'ú' or 'ù' or 'ủ' or 'ũ' or 'ụ' or
            'ư' or 'ứ' or 'ừ' or 'ử' or 'ữ' or 'ự' or
            'Ú' or 'Ù' or 'Ủ' or 'Ũ' or 'Ụ' or
            'Ư' or 'Ứ' or 'Ừ' or 'Ử' or 'Ữ' or 'Ự' or
            'û' or 'Û' or 'ü' or 'Ü' or 'ū' or 'Ū' or 'ų' or 'Ų' => 'U',

            // ýỳỷỹỵ / ÝỲỶỸỴ + common Latin -> 'Y'
            'ý' or 'ỳ' or 'ỷ' or 'ỹ' or 'ỵ' or
            'Ý' or 'Ỳ' or 'Ỷ' or 'Ỹ' or 'Ỵ' or
            'ÿ' or 'Ÿ' => 'Y',

            _ => char.ToUpperInvariant(c)
        };
    }

    private static int CountVisibleChars(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(text[i]) != UnicodeCategory.NonSpacingMark)
            {
                count++;
            }
        }
        return count;
    }

    private static int GetCharLengthForVisibleChars(ReadOnlySpan<char> text, int maxVisible)
    {
        if (maxVisible <= 0) return 0;
        int visible = 0;
        int i = 0;
        for (; i < text.Length; i++)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(text[i]) != UnicodeCategory.NonSpacingMark)
            {
                if (visible == maxVisible) break;
                visible++;
            }
        }
        while (i < text.Length && CharUnicodeInfo.GetUnicodeCategory(text[i]) == UnicodeCategory.NonSpacingMark)
        {
            i++;
        }
        return i;
    }

    public static int MeasureWidth(ReadOnlySpan<char> text, PixelFontSize size = PixelFontSize.Small)
    {
        int count = CountVisibleChars(text);
        if (count == 0) return 0;
        return count * CharAdvance(size) - 1;
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
            char raw = text[i];
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char c = NormalizeGlyphChar(raw);
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

        int visibleCount = CountVisibleChars(text);
        if (visibleCount <= maxChars)
        {
            DrawText(buf, x, y, text, color, size, shadowColor);
            return;
        }

        if (maxChars <= 2)
        {
            int sliceLen = GetCharLengthForVisibleChars(text, maxChars);
            DrawText(buf, x, y, text.Slice(0, sliceLen), color, size, shadowColor);
            return;
        }

        int keepVisible = maxChars - 2;
        int prefixLen = GetCharLengthForVisibleChars(text, keepVisible);
        DrawText(buf, x, y, text.Slice(0, prefixLen), color, size, shadowColor);
        DrawText(buf, x + keepVisible * advance, y, "..", color, size, shadowColor);
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

            int remainingVisible = CountVisibleChars(text.Slice(start));
            bool isLastLine = line == maxLines - 1;

            if (remainingVisible <= maxCharsPerLine)
            {
                DrawText(buf, x, y + line * lineStep, text.Slice(start), color, size, shadowColor);
                break;
            }

            if (isLastLine)
            {
                DrawTextTruncated(buf, x, y + line * lineStep, maxWidthPx, text.Slice(start), color, size, shadowColor);
                break;
            }

            int takeLen = GetCharLengthForVisibleChars(text.Slice(start), maxCharsPerLine);
            // Try to break on a space within maxCharsPerLine
            int breakIdx = -1;
            int searchEnd = Math.Min(start + takeLen, len);
            for (int i = searchEnd - 1; i > start; i--)
            {
                if (text[i] == ' ')
                {
                    breakIdx = i;
                    break;
                }
            }

            int take = breakIdx > start ? breakIdx - start : takeLen;
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
