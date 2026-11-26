using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KageDesk.Rendering.Canvas;

public sealed class PixelBuffer : IDisposable
{
    private readonly int[] _pixels;
    private readonly GCHandle _handle;
    private readonly Bitmap _bitmap;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    public Bitmap Bitmap
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bitmap;
        }
    }

    public Span<int> Pixels => _pixels.AsSpan();
    public ReadOnlySpan<int> ReadOnlyPixels => _pixels;

    public PixelBuffer(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        _pixels = new int[width * height];
        _handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, _handle.AddrOfPinnedObject());
        }
        catch
        {
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
            throw;
        }
    }

    public void Clear(int argb)
    {
        Array.Fill(_pixels, ColorUtil.ToPremultiplied(argb));
    }

    public void ClearTransparent()
    {
        Array.Clear(_pixels, 0, _pixels.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
        return _pixels[y * Width + x];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixel(int x, int y, int argb)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        _pixels[y * Width + x] = ColorUtil.ToPremultiplied(argb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BlendPixel(int x, int y, int straightArgb)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        int idx = y * Width + x;
        _pixels[idx] = ColorUtil.BlendOverPremultiplied(_pixels[idx], straightArgb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BlendPixel(int x, int y, int rgb, int alpha)
    {
        if (alpha <= 0) return;
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        int idx = y * Width + x;
        _pixels[idx] = ColorUtil.BlendOverPremultiplied(_pixels[idx], ColorUtil.WithAlpha(rgb, alpha));
    }

    public void FillRect(int x, int y, int w, int h, int argb)
    {
        if (w <= 0 || h <= 0) return;

        int x0 = x < 0 ? 0 : x;
        int y0 = y < 0 ? 0 : y;
        int x1 = x + w;
        int y1 = y + h;
        if (x1 > Width) x1 = Width;
        if (y1 > Height) y1 = Height;
        if (x0 >= x1 || y0 >= y1) return;

        int premul = ColorUtil.ToPremultiplied(argb);
        int spanLen = x1 - x0;
        Span<int> pixels = _pixels;
        for (int row = y0; row < y1; row++)
        {
            pixels.Slice(row * Width + x0, spanLen).Fill(premul);
        }
    }

    public void BlendRect(int x, int y, int w, int h, int straightArgb)
    {
        int sa = (straightArgb >> 24) & 0xFF;
        if (sa == 0 || w <= 0 || h <= 0) return;
        if (sa == 255)
        {
            FillRect(x, y, w, h, straightArgb);
            return;
        }

        int x0 = x < 0 ? 0 : x;
        int y0 = y < 0 ? 0 : y;
        int x1 = x + w;
        int y1 = y + h;
        if (x1 > Width) x1 = Width;
        if (y1 > Height) y1 = Height;
        if (x0 >= x1 || y0 >= y1) return;

        int sr = (straightArgb >> 16) & 0xFF;
        int sg = (straightArgb >> 8) & 0xFF;
        int sb = straightArgb & 0xFF;
        int srp = ColorUtil.Div255(sr * sa);
        int sgp = ColorUtil.Div255(sg * sa);
        int sbp = ColorUtil.Div255(sb * sa);
        int invSa = 255 - sa;
        int srcPremul = (sa << 24) | (srp << 16) | (sgp << 8) | sbp;

        int[] pixels = _pixels;
        int width = Width;
        for (int row = y0; row < y1; row++)
        {
            int rowStart = row * width;
            for (int col = x0; col < x1; col++)
            {
                int idx = rowStart + col;
                int dstPArgb = pixels[idx];
                int da = (dstPArgb >> 24) & 0xFF;
                if (da == 0)
                {
                    pixels[idx] = srcPremul;
                    continue;
                }

                int oa = sa + ColorUtil.Div255(da * invSa);
                int or_ = srp + ColorUtil.Div255(((dstPArgb >> 16) & 0xFF) * invSa);
                int og = sgp + ColorUtil.Div255(((dstPArgb >> 8) & 0xFF) * invSa);
                int ob = sbp + ColorUtil.Div255((dstPArgb & 0xFF) * invSa);

                if (oa > 255) oa = 255;
                if (or_ > oa) or_ = oa;
                if (og > oa) og = oa;
                if (ob > oa) ob = oa;

                pixels[idx] = (oa << 24) | (or_ << 16) | (og << 8) | ob;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BlendRect(int x, int y, int w, int h, int rgb, int alpha)
    {
        if (alpha <= 0) return;
        BlendRect(x, y, w, h, ColorUtil.WithAlpha(rgb, alpha));
    }

    public void FillVerticalGradient(int x, int y, int w, int h, int topRgb, int bottomRgb)
    {
        if (w <= 0 || h <= 0) return;
        int x0 = x < 0 ? 0 : x;
        int x1 = x + w > Width ? Width : x + w;
        if (x0 >= x1) return;

        int spanLen = x1 - x0;
        Span<int> pixels = _pixels;
        float denom = h > 1 ? h - 1 : 1f;

        for (int i = 0; i < h; i++)
        {
            int py = y + i;
            if ((uint)py >= (uint)Height) continue;
            int color = ColorUtil.ToPremultiplied(ColorUtil.Lerp(topRgb, bottomRgb, i / denom));
            pixels.Slice(py * Width + x0, spanLen).Fill(color);
        }
    }

    public void DrawOutlineRect(int x, int y, int w, int h, int argb, int thickness = 1)
    {
        if (w <= 0 || h <= 0 || thickness <= 0) return;
        int a = (argb >> 24) & 0xFF;
        bool blend = a > 0 && a < 255;

        if (blend)
        {
            BlendRect(x, y, w, thickness, argb);
            if (h > thickness)
                BlendRect(x, y + h - thickness, w, thickness, argb);
            int innerH = h - thickness * 2;
            if (innerH > 0)
            {
                BlendRect(x, y + thickness, thickness, innerH, argb);
                if (w > thickness)
                    BlendRect(x + w - thickness, y + thickness, thickness, innerH, argb);
            }
        }
        else
        {
            FillRect(x, y, w, thickness, argb);
            if (h > thickness)
                FillRect(x, y + h - thickness, w, thickness, argb);
            int innerH = h - thickness * 2;
            if (innerH > 0)
            {
                FillRect(x, y + thickness, thickness, innerH, argb);
                if (w > thickness)
                    FillRect(x + w - thickness, y + thickness, thickness, innerH, argb);
            }
        }
    }

    public void DrawPixelCard(int x, int y, int w, int h, int fillArgb, int borderArgb, int innerHighlightArgb = 0)
    {
        if (w < 4 || h < 4) return;
        // Cut 1px corners for crisp retro pixel-art panel feel
        FillRect(x + 1, y, w - 2, 1, borderArgb);
        FillRect(x + 1, y + h - 1, w - 2, 1, borderArgb);
        FillRect(x, y + 1, 1, h - 2, borderArgb);
        FillRect(x + w - 1, y + 1, 1, h - 2, borderArgb);
        FillRect(x + 1, y + 1, w - 2, h - 2, fillArgb);

        if (innerHighlightArgb != 0)
        {
            BlendRect(x + 2, y + 1, w - 4, 1, innerHighlightArgb);
        }
    }

    public void DrawLine(int x0, int y0, int x1, int y1, int argb)
    {
        int a = (argb >> 24) & 0xFF;
        bool blend = a > 0 && a < 255;
        int premul = blend ? 0 : ColorUtil.ToPremultiplied(argb);

        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if ((uint)x0 < (uint)Width && (uint)y0 < (uint)Height)
            {
                int idx = y0 * Width + x0;
                _pixels[idx] = blend
                    ? ColorUtil.BlendOverPremultiplied(_pixels[idx], argb)
                    : premul;
            }

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    public void DrawCircle(int cx, int cy, int radius, int argb, bool filled = true)
    {
        if (radius < 0) return;
        int a = (argb >> 24) & 0xFF;
        bool blend = a > 0 && a < 255;

        if (radius == 0)
        {
            if (blend) BlendPixel(cx, cy, argb);
            else SetPixel(cx, cy, argb);
            return;
        }

        int rSq = radius * radius;
        if (filled)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int py = cy + dy;
                if ((uint)py >= (uint)Height) continue;
                int maxDx = (int)Math.Sqrt(rSq - dy * dy);
                int startX = cx - maxDx;
                int span = maxDx * 2 + 1;
                if (blend) BlendRect(startX, py, span, 1, argb);
                else FillRect(startX, py, span, 1, argb);
            }
        }
        else
        {
            int x = radius;
            int y = 0;
            int err = 1 - x;

            while (x >= y)
            {
                PlotCircleOctants(cx, cy, x, y, argb, blend);
                y++;
                if (err < 0)
                {
                    err += 2 * y + 1;
                }
                else
                {
                    x--;
                    err += 2 * (y - x) + 1;
                }
            }
        }
    }

    private void PlotCircleOctants(int cx, int cy, int x, int y, int argb, bool blend)
    {
        if (blend)
        {
            BlendPixel(cx + x, cy + y, argb);
            BlendPixel(cx + y, cy + x, argb);
            BlendPixel(cx - y, cy + x, argb);
            BlendPixel(cx - x, cy + y, argb);
            BlendPixel(cx - x, cy - y, argb);
            BlendPixel(cx - y, cy - x, argb);
            BlendPixel(cx + y, cy - x, argb);
            BlendPixel(cx + x, cy - y, argb);
        }
        else
        {
            SetPixel(cx + x, cy + y, argb);
            SetPixel(cx + y, cy + x, argb);
            SetPixel(cx - y, cy + x, argb);
            SetPixel(cx - x, cy + y, argb);
            SetPixel(cx - x, cy - y, argb);
            SetPixel(cx - y, cy - x, argb);
            SetPixel(cx + y, cy - x, argb);
            SetPixel(cx + x, cy - y, argb);
        }
    }

    public void BlitNearestNeighborTo(PixelBuffer destination, int scale)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        int expectedW = Width * scale;
        int expectedH = Height * scale;
        if (destination.Width != expectedW || destination.Height != expectedH)
        {
            throw new ArgumentException($"Destination must be {expectedW}x{expectedH} for scale {scale}.", nameof(destination));
        }

        int[] src = _pixels;
        int[] dst = destination._pixels;
        int srcW = Width;
        int srcH = Height;
        int dstW = destination.Width;

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
                Array.Copy(dst, firstDstRow, dst, (dstBaseY + ry) * dstW, dstW);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bitmap.Dispose();
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }
}
