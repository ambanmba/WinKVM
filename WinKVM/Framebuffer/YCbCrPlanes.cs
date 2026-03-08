using System.Runtime.InteropServices;

namespace WinKVM.Framebuffer;

/// YCbCr 4:2:0 planar buffer produced by ICT decode.
/// Uploaded to GPU as three separate R8 textures for HLSL YCbCr→RGB conversion.
public sealed class YCbCrPlanes : IDisposable
{
    public int Width  { get; }
    public int Height { get; }

    // Y plane: full resolution
    public int YStride  => Width;
    public int YSize    => Width * Height;

    // Cb/Cr planes: half resolution (4:2:0)
    public int CStride  => (Width  + 1) / 2;
    public int CHeight  => (Height + 1) / 2;
    public int CSize    => CStride * CHeight;

    // Native aligned buffers — zero-copy upload to GPU on unified-memory adapters
    private nint _yBuf, _cbBuf, _crBuf;
    private bool _disposed;

    public unsafe byte* Y  => _disposed ? null : (byte*)_yBuf;
    public unsafe byte* Cb => _disposed ? null : (byte*)_cbBuf;
    public unsafe byte* Cr => _disposed ? null : (byte*)_crBuf;

    // Span accessors for fixed() pinning in ICTDecoder
    public unsafe Span<byte> YSpan  => _disposed ? Span<byte>.Empty : new Span<byte>((byte*)_yBuf,  YSize);
    public unsafe Span<byte> CbSpan => _disposed ? Span<byte>.Empty : new Span<byte>((byte*)_cbBuf, CSize);
    public unsafe Span<byte> CrSpan => _disposed ? Span<byte>.Empty : new Span<byte>((byte*)_crBuf, CSize);

    public unsafe YCbCrPlanes(int width, int height)
    {
        Width  = width;
        Height = height;
        _yBuf  = (nint)NativeMemory.AlignedAlloc((nuint)YSize, 16);
        _cbBuf = (nint)NativeMemory.AlignedAlloc((nuint)CSize, 16);
        _crBuf = (nint)NativeMemory.AlignedAlloc((nuint)CSize, 16);
        // Init to neutral gray (Y=128, Cb=128, Cr=128) so skipped inter-frame tiles
        // show as gray rather than garbage on the first frame.
        NativeMemory.Fill((void*)_yBuf,  (nuint)YSize, 128);
        NativeMemory.Fill((void*)_cbBuf, (nuint)CSize, 128);
        NativeMemory.Fill((void*)_crBuf, (nuint)CSize, 128);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        unsafe
        {
            NativeMemory.AlignedFree((void*)_yBuf);
            NativeMemory.AlignedFree((void*)_cbBuf);
            NativeMemory.AlignedFree((void*)_crBuf);
        }
    }
}
