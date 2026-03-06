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

    public YCbCrPlanes(int width, int height)
    {
        Width  = width;
        Height = height;
        _yBuf  = (nint)NativeMemory.AlignedAlloc((nuint)YSize,  16);
        _cbBuf = (nint)NativeMemory.AlignedAlloc((nuint)CSize,  16);
        _crBuf = (nint)NativeMemory.AlignedAlloc((nuint)CSize,  16);
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
