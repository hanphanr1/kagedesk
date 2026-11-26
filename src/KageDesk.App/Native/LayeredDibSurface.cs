using System.Drawing;
using System.Runtime.InteropServices;
using KageDesk.Rendering.Canvas;

namespace KageDesk.App.Native;

public sealed class LayeredDibSurface : IDisposable
{
    private IntPtr _memDc = IntPtr.Zero;
    private IntPtr _hBitmap = IntPtr.Zero;
    private IntPtr _oldBitmap = IntPtr.Zero;
    private IntPtr _bits = IntPtr.Zero;
    private int _width;
    private int _height;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;
    public IntPtr MemDc => _memDc;
    public IntPtr Bits => _bits;
    public IntPtr HBitmap => _hBitmap;

    public LayeredDibSurface(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        AllocateSurface(width, height, out _memDc, out _hBitmap, out _oldBitmap, out _bits);
        _width = width;
        _height = height;
    }

    public void Resize(int newWidth, int newHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (newWidth <= 0) throw new ArgumentOutOfRangeException(nameof(newWidth));
        if (newHeight <= 0) throw new ArgumentOutOfRangeException(nameof(newHeight));

        if (_width == newWidth && _height == newHeight && _memDc != IntPtr.Zero && _hBitmap != IntPtr.Zero)
        {
            return;
        }

        // 1. Allocate NEW surface first
        AllocateSurface(newWidth, newHeight, out var newMemDc, out var newHBitmap, out var newOldBitmap, out var newBits);

        // 2. Capture old handles for safe destruction
        var oldMemDc = _memDc;
        var oldHBitmap = _hBitmap;
        var oldSavedBitmap = _oldBitmap;

        // 3. Swap in new handles ONLY after full success
        _memDc = newMemDc;
        _hBitmap = newHBitmap;
        _oldBitmap = newOldBitmap;
        _bits = newBits;
        _width = newWidth;
        _height = newHeight;

        // 4. Safely tear down old surface
        TearDownSurface(oldMemDc, oldHBitmap, oldSavedBitmap);
    }

    public unsafe void CopyFrom(PixelBuffer buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Width != _width || buffer.Height != _height)
        {
            throw new ArgumentException($"Buffer dimensions ({buffer.Width}x{buffer.Height}) do not match surface ({_width}x{_height}).");
        }

        int byteCount = _width * _height * 4;
        fixed (int* srcPtr = buffer.ReadOnlyPixels)
        {
            Buffer.MemoryCopy(srcPtr, (void*)_bits, byteCount, byteCount);
        }
    }

    public unsafe void BlitNearestNeighborFrom(PixelBuffer source, int scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));

        if (scale == 1)
        {
            CopyFrom(source);
            return;
        }

        int expectedW = source.Width * scale;
        int expectedH = source.Height * scale;
        if (_width != expectedW || _height != expectedH)
        {
            throw new ArgumentException($"Surface size {_width}x{_height} does not match expected {expectedW}x{expectedH} for scale {scale}.");
        }

        int srcW = source.Width;
        int srcH = source.Height;
        int dstW = _width;
        long rowBytes = (long)dstW * sizeof(int);

        fixed (int* src = source.ReadOnlyPixels)
        {
            int* dst = (int*)_bits;

            if (scale == 2)
            {
                for (int sy = 0; sy < srcH; sy++)
                {
                    int* srcRowPtr = src + sy * srcW;
                    int* dstRow0 = dst + (sy * 2) * dstW;
                    ulong* dstRow0Pairs = (ulong*)dstRow0;

                    for (int sx = 0; sx < srcW; sx++)
                    {
                        uint px = (uint)srcRowPtr[sx];
                        dstRow0Pairs[sx] = px | ((ulong)px << 32);
                    }

                    Buffer.MemoryCopy(dstRow0, dstRow0 + dstW, rowBytes, rowBytes);
                }
                return;
            }

            for (int sy = 0; sy < srcH; sy++)
            {
                int srcRow = sy * srcW;
                int dstBaseY = sy * scale;
                int firstDstRow = dstBaseY * dstW;

                for (int sx = 0; sx < srcW; sx++)
                {
                    int px = src[srcRow + sx];
                    int dstX = sx * scale;
                    for (int k = 0; k < scale; k++)
                    {
                        dst[firstDstRow + dstX + k] = px;
                    }
                }

                for (int ry = 1; ry < scale; ry++)
                {
                    Buffer.MemoryCopy(
                        dst + firstDstRow,
                        dst + (dstBaseY + ry) * dstW,
                        rowBytes,
                        rowBytes);
                }
            }
        }
    }

    /// <summary>
    /// Zero-allocation nearest-neighbor blit supporting arbitrary per-monitor DPI scaled dimensions
    /// (e.g. 125%, 150%, 175%, 200% DPI), delegating to fast paths for exact 1x and 2x sizes.
    /// </summary>
    public unsafe void BlitNearestNeighborScaledFrom(PixelBuffer source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        int srcW = source.Width;
        int srcH = source.Height;
        int dstW = _width;
        int dstH = _height;

        if (dstW == srcW && dstH == srcH)
        {
            CopyFrom(source);
            return;
        }

        if (dstW == srcW * 2 && dstH == srcH * 2)
        {
            BlitNearestNeighborFrom(source, 2);
            return;
        }

        // 16.16 fixed-point nearest-neighbor scaling (zero heap allocations)
        uint stepX = (uint)(((long)srcW << 16) / dstW);
        uint stepY = (uint)(((long)srcH << 16) / dstH);
        long rowBytes = (long)dstW * sizeof(int);

        fixed (int* src = source.ReadOnlyPixels)
        {
            int* dst = (int*)_bits;
            int prevSy = -1;
            int* prevDstRow = null;
            uint accY = 0;

            for (int dy = 0; dy < dstH; dy++)
            {
                int sy = (int)(accY >> 16);
                if (sy >= srcH) sy = srcH - 1;
                accY += stepY;

                int* dstRow = dst + dy * dstW;
                if (sy == prevSy && prevDstRow != null)
                {
                    Buffer.MemoryCopy(prevDstRow, dstRow, rowBytes, rowBytes);
                    continue;
                }

                int* srcRow = src + sy * srcW;
                uint accX = 0;
                for (int dx = 0; dx < dstW; dx++)
                {
                    int sx = (int)(accX >> 16);
                    if (sx >= srcW) sx = srcW - 1;
                    dstRow[dx] = srcRow[sx];
                    accX += stepX;
                }

                prevSy = sy;
                prevDstRow = dstRow;
            }
        }
    }

    public bool UpdateLayeredWindow(IntPtr hwnd, int dstX, int dstY, byte alpha = 255)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hwnd == IntPtr.Zero || !Win32Interop.IsWindow(hwnd)) return false;

        IntPtr screenDc = Win32Interop.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        try
        {
            var blend = new Win32Interop.BLENDFUNCTION
            {
                BlendOp = Win32Interop.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = Win32Interop.AC_SRC_ALPHA
            };

            var ptSrc = new Win32Interop.POINT(0, 0);
            var ptDst = new Win32Interop.POINT(dstX, dstY);
            var szDst = new Win32Interop.SIZE(_width, _height);

            return Win32Interop.UpdateLayeredWindow(
                hwnd,
                screenDc,
                ref ptDst,
                ref szDst,
                _memDc,
                ref ptSrc,
                0,
                ref blend,
                Win32Interop.ULW_ALPHA);
        }
        finally
        {
            Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void AllocateSurface(
        int width,
        int height,
        out IntPtr memDc,
        out IntPtr hBitmap,
        out IntPtr oldBitmap,
        out IntPtr bits)
    {
        IntPtr screenDc = Win32Interop.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to acquire screen DC for DIB creation.");
        }

        memDc = IntPtr.Zero;
        hBitmap = IntPtr.Zero;
        oldBitmap = IntPtr.Zero;
        bits = IntPtr.Zero;

        try
        {
            memDc = Win32Interop.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateCompatibleDC failed with Win32 error {Marshal.GetLastWin32Error()}");
            }

            var bmi = new Win32Interop.BITMAPINFO
            {
                bmiHeader = new Win32Interop.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down 32bpp DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32Interop.BI_RGB
                }
            };

            hBitmap = Win32Interop.CreateDIBSection(
                memDc,
                ref bmi,
                Win32Interop.DIB_RGB_COLORS,
                out bits,
                IntPtr.Zero,
                0);

            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateDIBSection failed with Win32 error {Marshal.GetLastWin32Error()}");
            }

            oldBitmap = Win32Interop.SelectObject(memDc, hBitmap);
            if (oldBitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException($"SelectObject into memDc failed with Win32 error {Marshal.GetLastWin32Error()}");
            }
        }
        catch
        {
            TearDownSurface(memDc, hBitmap, oldBitmap);
            throw;
        }
        finally
        {
            Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void TearDownSurface(IntPtr memDc, IntPtr hBitmap, IntPtr oldBitmap)
    {
        if (memDc != IntPtr.Zero)
        {
            if (oldBitmap != IntPtr.Zero)
            {
                IntPtr restored = Win32Interop.SelectObject(memDc, oldBitmap);
                if (restored == IntPtr.Zero)
                {
                    // SelectObject restore failed: delete DC first then delete object
                    Win32Interop.DeleteDC(memDc);
                    memDc = IntPtr.Zero;
                    if (hBitmap != IntPtr.Zero)
                    {
                        Win32Interop.DeleteObject(hBitmap);
                        hBitmap = IntPtr.Zero;
                    }
                }
            }

            if (memDc != IntPtr.Zero)
            {
                Win32Interop.DeleteDC(memDc);
            }
        }

        if (hBitmap != IntPtr.Zero)
        {
            Win32Interop.DeleteObject(hBitmap);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TearDownSurface(_memDc, _hBitmap, _oldBitmap);
        _memDc = IntPtr.Zero;
        _hBitmap = IntPtr.Zero;
        _oldBitmap = IntPtr.Zero;
        _bits = IntPtr.Zero;
    }
}
