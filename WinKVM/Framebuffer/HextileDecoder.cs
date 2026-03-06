namespace WinKVM.Framebuffer;

/// GPU-direct commands produced by hextile decoding.
/// Passed to the D3D11 renderer for GPU-side execution instead of CPU pixel writes.
public record struct FillCommand(ushort X, ushort Y, ushort W, ushort H, uint ColorBgrx);
public record struct RawTileCommand(ushort DstX, ushort DstY, ushort Width, ushort Height, int DataOffset);

/// Decodes RFB Hextile encoding.
/// Produces GPU-direct fill/raw commands where possible; falls back to CPU writes for small tiles.
public sealed class HextileDecoder
{
    private uint   _bg;
    private uint   _fg;

    public void Reset() { _bg = 0; _fg = 0; }

    /// Decode a Hextile-encoded rectangle.
    /// Returns lists of GPU fill commands and raw tile uploads for batch GPU submission.
    public (List<FillCommand> fills, List<RawTileCommand> rawTiles, byte[] rawTileData)
        Decode(ReadOnlySpan<byte> data, KvmFramebuffer fb,
               int rectX, int rectY, int rectW, int rectH)
    {
        var fills      = new List<FillCommand>();
        var rawTiles   = new List<RawTileCommand>();
        var rawDataBuf = new System.IO.MemoryStream();

        int pos = 0;

        for (int ty = rectY; ty < rectY + rectH; ty += 16)
        for (int tx = rectX; tx < rectX + rectW; tx += 16)
        {
            int tw = Math.Min(16, rectX + rectW - tx);
            int th = Math.Min(16, rectY + rectH - ty);

            if (pos >= data.Length) break;
            byte flags = data[pos++];

            if ((flags & HextileFlags.Raw) != 0)
            {
                int tileBytes = tw * th * 4;
                if (pos + tileBytes > data.Length) break;
                var tileData = data.Slice(pos, tileBytes);

                // Queue raw tile for GPU DMA upload
                int offset = (int)rawDataBuf.Length;
                rawDataBuf.Write(tileData);
                rawTiles.Add(new RawTileCommand((ushort)tx, (ushort)ty, (ushort)tw, (ushort)th, offset));
                pos += tileBytes;
                continue;
            }

            if ((flags & HextileFlags.BackgroundSpecified) != 0)
            {
                _bg = ReadPixel(data, ref pos);
            }

            // Fill whole tile with background colour
            fills.Add(new FillCommand((ushort)tx, (ushort)ty, (ushort)tw, (ushort)th, _bg));

            if ((flags & HextileFlags.ForegroundSpecified) != 0)
            {
                _fg = ReadPixel(data, ref pos);
            }

            if ((flags & HextileFlags.AnySubrects) == 0) continue;

            if (pos >= data.Length) break;
            int nSubrects = data[pos++];
            bool coloured = (flags & HextileFlags.SubrectsColoured) != 0;

            for (int s = 0; s < nSubrects; s++)
            {
                uint color = _fg;
                if (coloured)
                {
                    if (pos + 4 > data.Length) break;
                    color = ReadPixel(data, ref pos);
                }
                if (pos + 2 > data.Length) break;
                byte xy = data[pos++];
                byte wh = data[pos++];

                int sx = tx + (xy >> 4);
                int sy = ty + (xy & 0xF);
                int sw = ((wh >> 4) & 0xF) + 1;
                int sh = (wh & 0xF) + 1;

                fills.Add(new FillCommand((ushort)sx, (ushort)sy, (ushort)sw, (ushort)sh, color));
            }
        }

        return (fills, rawTiles, rawDataBuf.ToArray());
    }

    private static uint ReadPixel(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 4 > data.Length) return 0;
        // BGRX little-endian: byte order [B, G, R, X]
        uint v = (uint)(data[pos] | (data[pos+1] << 8) | (data[pos+2] << 16) | (data[pos+3] << 24));
        pos += 4;
        return v;
    }
}
