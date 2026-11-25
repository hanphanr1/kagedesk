using System.IO;

namespace KageDesk.Rendering.Export;

public sealed class Gif89aEncoder
{
    private readonly Stream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly int _scale;
    private readonly int _outWidth;
    private readonly int _outHeight;
    private bool _headerWritten;

    // Bit-packing buffer
    private int _bitAccumulator;
    private int _bitsCount;

    // Sub-block buffer (max 254 bytes)
    private readonly byte[] _subBlock = new byte[255];
    private int _subBlockCount;

    // LZW Hash Table
    private const int HashSize = 16384;
    private const int HashMask = HashSize - 1;
    private readonly int[] _hashKeys = new int[HashSize];
    private readonly short[] _hashValues = new short[HashSize];

    public Gif89aEncoder(Stream stream, int width, int height, int scale = 1)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be at least 1.");

        int outWidth = checked(width * scale);
        int outHeight = checked(height * scale);

        if (outWidth > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), "Scaled width exceeds max GIF dimension (65535).");
        if (outHeight > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(height), "Scaled height exceeds max GIF dimension (65535).");

        _width = width;
        _height = height;
        _scale = scale;
        _outWidth = outWidth;
        _outHeight = outHeight;
    }

    private int[]? _prevArgb;

    public void AddFrame(int[] argbPixels, int delayCentiseconds = 8)
    {
        ArgumentNullException.ThrowIfNull(argbPixels);
        if (argbPixels.Length != _width * _height)
        {
            throw new ArgumentException($"Pixel array length {argbPixels.Length} does not match expected {_width}x{_height} ({_width * _height}).", nameof(argbPixels));
        }
        if (delayCentiseconds < 1 || delayCentiseconds > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(delayCentiseconds), $"Delay {delayCentiseconds} must be between 1 and {ushort.MaxValue} centiseconds.");
        }

        if (!_headerWritten)
        {
            WriteHeader();
            _headerWritten = true;
        }

        bool isFirstFrame = _prevArgb == null;
        byte[] palette = new byte[768]; // 256 * 3 (RGB)
        byte[] indexedPixels = QuantizeFrame(argbPixels, palette, !isFirstFrame, _prevArgb, out bool hasTransparency);

        // 2. Graphic Control Extension (delay & disposal)
        _stream.WriteByte(0x21); // Extension Introducer
        _stream.WriteByte(0xF9); // Graphic Control Label
        _stream.WriteByte(0x04); // Block size = 4
        // 0x04 = leave in place (opaque), 0x05 = leave in place + transparent flag
        _stream.WriteByte((byte)(hasTransparency ? 0x05 : 0x04));
        WriteShort((ushort)delayCentiseconds);
        _stream.WriteByte(0x00); // Transparent Color Index = 0
        _stream.WriteByte(0x00); // Block Terminator

        // 3. Image Descriptor
        _stream.WriteByte(0x2C); // Image Separator
        WriteShort(0); // Left
        WriteShort(0); // Top
        WriteShort((ushort)_outWidth);
        WriteShort((ushort)_outHeight);
        _stream.WriteByte(0x87); // Local Color Table Flag (0x80) | 256 colors (0x07)

        // 4. Local Color Table (768 bytes)
        _stream.Write(palette, 0, 768);

        // 5. LZW Image Data
        WriteLzwData(indexedPixels);

        _prevArgb = (int[])argbPixels.Clone();
    }

    public void Finish()
    {
        if (_headerWritten)
        {
            _stream.WriteByte(0x3B); // GIF Trailer
            _stream.Flush();
        }
    }

    private void WriteHeader()
    {
        // 1. Header "GIF89a"
        ReadOnlySpan<byte> gif89a = "GIF89a"u8;
        _stream.Write(gif89a);

        // 2. Logical Screen Descriptor
        WriteShort((ushort)_outWidth);
        WriteShort((ushort)_outHeight);
        _stream.WriteByte(0x70); // Packed: No Global Color Table, Color Resolution 8 bits (0x70)
        _stream.WriteByte(0x00); // Background Color Index
        _stream.WriteByte(0x00); // Pixel Aspect Ratio

        // 3. Netscape 2.0 Application Extension (for infinite loop)
        _stream.WriteByte(0x21); // Extension Introducer
        _stream.WriteByte(0xFF); // Application Extension Label
        _stream.WriteByte(0x0B); // Block Size (11)
        ReadOnlySpan<byte> netscape = "NETSCAPE2.0"u8;
        _stream.Write(netscape);
        _stream.WriteByte(0x03); // Sub-block data length (3)
        _stream.WriteByte(0x01); // Sub-block ID (1)
        WriteShort(0);          // Loop count (0 = infinite)
        _stream.WriteByte(0x00); // Block Terminator
    }

    private byte[] QuantizeFrame(int[] sourceArgb, byte[] palette, bool useTransparency, int[]? prevArgb, out bool hasTransparency)
    {
        int totalPixels = _outWidth * _outHeight;
        byte[] indexed = new byte[totalPixels];

        int startIndex = useTransparency ? 1 : 0;

        // Collect unique colors
        var uniqueColors = new Dictionary<uint, byte>();
        int colorCount = startIndex;

        for (int i = 0; i < sourceArgb.Length; i++)
        {
            if (useTransparency && prevArgb != null && sourceArgb[i] == prevArgb[i])
            {
                continue; // Unchanged pixel will use transparent index 0
            }

            uint c = (uint)sourceArgb[i] | 0xFF000000;
            if (!uniqueColors.ContainsKey(c))
            {
                if (colorCount < 256)
                {
                    uniqueColors[c] = (byte)colorCount;
                    palette[colorCount * 3] = (byte)((c >> 16) & 0xFF);     // R
                    palette[colorCount * 3 + 1] = (byte)((c >> 8) & 0xFF);  // G
                    palette[colorCount * 3 + 2] = (byte)(c & 0xFF);         // B
                    colorCount++;
                }
                else
                {
                    colorCount = 999;
                    break;
                }
            }
        }

        uint colorMask = 0xFFFFFFFF;

        if (colorCount > 256)
        {
            colorMask = 0xFFF8F8F8;
            uniqueColors.Clear();
            colorCount = startIndex;
            for (int i = 0; i < sourceArgb.Length; i++)
            {
                if (useTransparency && prevArgb != null && sourceArgb[i] == prevArgb[i])
                {
                    continue;
                }

                uint c = ((uint)sourceArgb[i] | 0xFF000000) & colorMask;
                if (!uniqueColors.ContainsKey(c))
                {
                    if (colorCount < 256)
                    {
                        uniqueColors[c] = (byte)colorCount;
                        palette[colorCount * 3] = (byte)((c >> 16) & 0xFF);
                        palette[colorCount * 3 + 1] = (byte)((c >> 8) & 0xFF);
                        palette[colorCount * 3 + 2] = (byte)(c & 0xFF);
                        colorCount++;
                    }
                    else
                    {
                        colorCount = 999;
                        break;
                    }
                }
            }
        }

        if (colorCount > 256)
        {
            colorMask = 0xFFF0F0F0;
            uniqueColors.Clear();
            colorCount = startIndex;
            for (int i = 0; i < sourceArgb.Length; i++)
            {
                if (useTransparency && prevArgb != null && sourceArgb[i] == prevArgb[i])
                {
                    continue;
                }

                uint c = ((uint)sourceArgb[i] | 0xFF000000) & colorMask;
                if (!uniqueColors.ContainsKey(c))
                {
                    if (colorCount < 256)
                    {
                        uniqueColors[c] = (byte)colorCount;
                        palette[colorCount * 3] = (byte)((c >> 16) & 0xFF);
                        palette[colorCount * 3 + 1] = (byte)((c >> 8) & 0xFF);
                        palette[colorCount * 3 + 2] = (byte)(c & 0xFF);
                        colorCount++;
                    }
                    else
                    {
                        colorCount = 999;
                        break;
                    }
                }
            }
        }

        if (colorCount <= 256)
        {
            hasTransparency = useTransparency;
            int destIdx = 0;
            for (int y = 0; y < _outHeight; y++)
            {
                int srcY = y / _scale;
                int srcRowOffset = srcY * _width;

                for (int x = 0; x < _outWidth; x++)
                {
                    int srcX = x / _scale;
                    int srcOffset = srcRowOffset + srcX;

                    if (useTransparency && prevArgb != null && sourceArgb[srcOffset] == prevArgb[srcOffset])
                    {
                        indexed[destIdx++] = 0; // Transparent!
                    }
                    else
                    {
                        uint c = ((uint)sourceArgb[srcOffset] | 0xFF000000) & colorMask;
                        indexed[destIdx++] = uniqueColors.TryGetValue(c, out byte idx) ? idx : (byte)startIndex;
                    }
                }
            }
        }
        else
        {
            // Fallback: Uniform 6/8-bit RGB332 palette (fully opaque so changed dark/black pixels never drop to transparent)
            hasTransparency = false;
            for (int i = 0; i < 256; i++)
            {
                int r3 = (i >> 5) & 0x07;
                int g3 = (i >> 2) & 0x07;
                int b2 = i & 0x03;

                palette[i * 3] = (byte)((r3 * 255) / 7);
                palette[i * 3 + 1] = (byte)((g3 * 255) / 7);
                palette[i * 3 + 2] = (byte)((b2 * 255) / 3);
            }

            int destIdx = 0;
            for (int y = 0; y < _outHeight; y++)
            {
                int srcY = y / _scale;
                int srcRowOffset = srcY * _width;

                for (int x = 0; x < _outWidth; x++)
                {
                    int srcX = x / _scale;
                    uint c = (uint)sourceArgb[srcRowOffset + srcX];
                    int r = (int)((c >> 16) & 0xFF);
                    int g = (int)((c >> 8) & 0xFF);
                    int b = (int)(c & 0xFF);

                    int r3 = (r * 7 + 127) / 255;
                    int g3 = (g * 7 + 127) / 255;
                    int b2 = (b * 3 + 127) / 255;

                    indexed[destIdx++] = (byte)((r3 << 5) | (g3 << 2) | b2);
                }
            }
        }

        return indexed;
    }

    private void WriteLzwData(byte[] indexedPixels)
    {
        const int minCodeSize = 8;
        const int clearCode = 1 << minCodeSize; // 256
        const int eoiCode = clearCode + 1;      // 257

        _stream.WriteByte(minCodeSize);

        // Reset bit & sub-block state
        _bitAccumulator = 0;
        _bitsCount = 0;
        _subBlockCount = 0;

        Array.Fill(_hashValues, (short)-1);

        int codeSize = minCodeSize + 1; // Starts at 9
        int nextCode = eoiCode + 1;     // 258

        WriteBits(clearCode, codeSize);

        if (indexedPixels.Length == 0)
        {
            WriteBits(eoiCode, codeSize);
            FlushBits();
            FlushSubBlock();
            _stream.WriteByte(0x00);
            return;
        }

        int prefix = indexedPixels[0];

        for (int i = 1; i < indexedPixels.Length; i++)
        {
            int c = indexedPixels[i];
            int key = (prefix << 8) | c;

            // Hash lookup with linear probing
            int hash = (int)(((uint)key * 2654435761u) & HashMask);
            int foundCode = -1;

            while (_hashValues[hash] != -1)
            {
                if (_hashKeys[hash] == key)
                {
                    foundCode = _hashValues[hash];
                    break;
                }
                hash = (hash + 1) & HashMask;
            }

            if (foundCode != -1)
            {
                prefix = foundCode;
            }
            else
            {
                WriteBits(prefix, codeSize);

                if (nextCode < 4096)
                {
                    _hashKeys[hash] = key;
                    _hashValues[hash] = (short)nextCode;
                    nextCode++;

                    if (nextCode > (1 << codeSize) && codeSize < 12)
                    {
                        codeSize++;
                    }
                }
                else
                {
                    // Table is full, emit clear code and reset
                    WriteBits(clearCode, codeSize);
                    Array.Fill(_hashValues, (short)-1);
                    codeSize = minCodeSize + 1;
                    nextCode = eoiCode + 1;
                }

                prefix = c;
            }
        }

        WriteBits(prefix, codeSize);
        WriteBits(eoiCode, codeSize);

        FlushBits();
        FlushSubBlock();
        _stream.WriteByte(0x00); // Block Terminator
    }

    private void WriteBits(int code, int size)
    {
        _bitAccumulator |= (code << _bitsCount);
        _bitsCount += size;

        while (_bitsCount >= 8)
        {
            EmitSubBlockByte((byte)(_bitAccumulator & 0xFF));
            _bitAccumulator >>= 8;
            _bitsCount -= 8;
        }
    }

    private void FlushBits()
    {
        if (_bitsCount > 0)
        {
            EmitSubBlockByte((byte)(_bitAccumulator & 0xFF));
            _bitAccumulator = 0;
            _bitsCount = 0;
        }
    }

    private void EmitSubBlockByte(byte b)
    {
        _subBlock[_subBlockCount++] = b;
        if (_subBlockCount >= 254)
        {
            FlushSubBlock();
        }
    }

    private void FlushSubBlock()
    {
        if (_subBlockCount > 0)
        {
            _stream.WriteByte((byte)_subBlockCount);
            _stream.Write(_subBlock, 0, _subBlockCount);
            _subBlockCount = 0;
        }
    }

    private void WriteShort(ushort value)
    {
        _stream.WriteByte((byte)(value & 0xFF));
        _stream.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
