namespace WinKVM.Framebuffer;

/// Decodes RFB Raw encoding into the framebuffer.
/// The protocol sends raw pixel data matching the negotiated pixel format (BGRX 32bpp LE).
public static class RawDecoder
{
    public static unsafe void Decode(
        ReadOnlySpan<byte> data,
        KvmFramebuffer fb,
        int x, int y, int w, int h)
    {
        if (data.Length < w * h * 4) return;
        fixed (byte* src = data)
            fb.CopyRect(src, w * 4, data.Length, x, y, w, h);
    }
}
