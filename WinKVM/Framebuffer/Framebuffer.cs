using System.Runtime.InteropServices;

namespace WinKVM.Framebuffer;

/// BGRX pixel buffer for the remote framebuffer.
/// Pixels stored as [B, G, R, X] — native Direct3D BGRA layout, zero swizzle needed.
public sealed class KvmFramebuffer : IDisposable
{
    public int Width         { get; }
    public int Height        { get; }
    public int BytesPerPixel => 4;
    public int BytesPerRow   => Width * BytesPerPixel;
    public int TotalBytes    => Width * Height * BytesPerPixel;

    // Aligned native buffer — shared with Direct3D texture on integrated/unified GPUs.
    private readonly nint _pixels;
    private bool _disposed;

    public unsafe byte* Pixels => _disposed ? null : (byte*)_pixels;

    public unsafe KvmFramebuffer(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Framebuffer dimensions must be positive");
        Width  = width;
        Height = height;
        // 16-byte aligned allocation for SIMD friendliness
        _pixels = (nint)NativeMemory.AlignedAlloc((nuint)TotalBytes, 16);
        NativeMemory.Clear((void*)_pixels, (nuint)TotalBytes);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.AlignedFree((void*)_pixels);
    }

    // ── Pixel operations ────────────────────────────────────────────────────

    public unsafe void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        byte* p = Pixels + (y * Width + x) * BytesPerPixel;
        p[0] = b; p[1] = g; p[2] = r; p[3] = 255;
    }

    /// Copy a BGRX block into the framebuffer at (dstX, dstY).
    public unsafe void CopyRect(byte* srcData, int srcBytesPerRow, int srcSize,
                                int dstX, int dstY, int w, int h)
    {
        if (dstX < 0 || dstX >= Width || w <= 0 || h <= 0) return;
        int copyWidth = Math.Min(w, Width - dstX) * BytesPerPixel;
        if (copyWidth <= 0) return;

        int y0 = Math.Max(dstY, 0);
        int y1 = Math.Min(dstY + h, Height);
        if (y0 >= y1) return;

        int dstRowBytes = Width * BytesPerPixel;

        if (srcBytesPerRow == copyWidth && dstRowBytes == copyWidth)
        {
            int srcOffset = (y0 - dstY) * srcBytesPerRow;
            int total = (y1 - y0) * copyWidth;
            if (srcOffset + total > srcSize) return;
            Buffer.MemoryCopy(srcData + srcOffset, Pixels + y0 * dstRowBytes, total, total);
        }
        else
        {
            for (int dy = y0; dy < y1; dy++)
            {
                int srcOffset = (dy - dstY) * srcBytesPerRow;
                if (srcOffset + copyWidth > srcSize) continue;
                int dstOffset = dy * dstRowBytes + dstX * BytesPerPixel;
                Buffer.MemoryCopy(srcData + srcOffset, Pixels + dstOffset, copyWidth, copyWidth);
            }
        }
    }

    /// Fill a rectangle with a solid colour.
    public unsafe void FillRect(int x, int y, int w, int h, byte r, byte g, byte b)
    {
        int x0 = Math.Max(x, 0),  x1 = Math.Min(x + w, Width);
        int y0 = Math.Max(y, 0),  y1 = Math.Min(y + h, Height);
        int cw = x1 - x0, ch = y1 - y0;
        if (cw <= 0 || ch <= 0) return;

        // Build 4-byte BGRX pattern
        uint pattern = (uint)(b | (g << 8) | (r << 16) | (255 << 24));

        byte* firstRow = Pixels + (y0 * Width + x0) * BytesPerPixel;
        int rowBytes = cw * BytesPerPixel;

        // Fill first row with pattern
        uint* row32 = (uint*)firstRow;
        for (int i = 0; i < cw; i++) row32[i] = pattern;

        // Copy first row to subsequent rows
        int dstRowBytes = Width * BytesPerPixel;
        for (int dy = 1; dy < ch; dy++)
            Buffer.MemoryCopy(firstRow, firstRow + dy * dstRowBytes, rowBytes, rowBytes);
    }

    /// Return a Span over the entire pixel buffer.
    public unsafe Span<byte> AsSpan() => new((void*)_pixels, TotalBytes);

    /// Return a pointer to a specific row.
    public unsafe byte* RowPointer(int y) => Pixels + y * BytesPerRow;
}
