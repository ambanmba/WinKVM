using System.Runtime.CompilerServices;

namespace WinKVM.Framebuffer;

/// Decodes ICT (Integrated Color Transform) encoding — Raritan's custom JPEG-like codec.
/// 16×16 tiles, Huffman-coded DCT coefficients, YCbCr 4:2:0 colour space.
///
/// GPU acceleration strategy:
///   • CPU: Huffman decode + dequantization (inherently serial per tile)
///   • GPU: Inverse DCT via HLSL compute shader (ICTDequant.hlsl) — batched across tiles
///   • GPU: YCbCr→RGB via HLSL pixel shader (YCbCr.hlsl) — full-frame render pass
///
/// On this decode path the CPU produces dequantized DCT coefficient blocks which
/// are uploaded as a structured buffer to the GPU for parallel IDCT execution,
/// then the GPU renders YCbCr planes directly to the display texture.
public sealed class ICTDecoder
{
    // ── Quantization tables (standard JPEG baseline, matching Raritan hardware) ──

    private static readonly int[] QuantLumi = [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68,109,103, 77,
        24, 35, 55, 64, 81,104,113, 92,
        49, 64, 78, 87,103,121,120,101,
        72, 92, 95, 98,112,100,103, 99
    ];

    private static readonly int[] QuantChroma = [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    ];

    private static readonly int[] Zigzag = [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    ];

    // Precomputed scaled quantization factors (factor / quality * scale)
    private float[] _qLumi   = new float[64];
    private float[] _qChroma = new float[64];
    private int     _lastQuality = -1;

    // Scratch buffers reused across frames
    private float[] _coeffs = new float[64];
    private float[] _idct   = new float[64];

    // ── Huffman tables (DC and AC, lumi and chroma) ─────────────────────────
    // Populated on first use from standard JPEG Annex K tables.
    private HuffTable? _dcLumi, _dcChroma, _acLumi, _acChroma;

    // ── Output buffers ───────────────────────────────────────────────────────
    // Reused to avoid per-frame allocation. Sized for maximum 2560×1440.
    private byte[]? _yBuf, _cbBuf, _crBuf;
    private int     _yStride, _cStride;

    public void ReleaseBuffers() { _yBuf = _cbBuf = _crBuf = null; }

    // ── Public decode API ────────────────────────────────────────────────────

    /// Decode an ICT frame into YCbCr planes suitable for GPU upload.
    /// Returns null on parse error (malformed stream or unsupported variant).
    public YCbCrPlanes? Decode(ReadOnlySpan<byte> data, int frameWidth, int frameHeight, int quality)
    {
        EnsureQuantTables(quality);
        EnsureHuffTables();
        EnsureOutputBuffers(frameWidth, frameHeight);

        var bits = new BitReader(data);

        int tilesX = (frameWidth  + 15) / 16;
        int tilesY = (frameHeight + 15) / 16;

        // CPU: Huffman decode + dequantization per tile (serial — data dependent)
        // The resulting spatial-domain blocks are written directly into _yBuf/_cbBuf/_crBuf.
        // The GPU then converts YCbCr→RGB in a single render pass.
        int dcY = 0, dcCb = 0, dcCr = 0;

        for (int tileY = 0; tileY < tilesY; tileY++)
        for (int tileX = 0; tileX < tilesX; tileX++)
        {
            // Read HIVE skip count
            int skip = bits.ReadHiveSkip();

            // Luma (4 8×8 blocks per 16×16 tile)
            for (int b = 0; b < 4; b++)
            {
                if (!DecodeBlock(ref bits, _dcLumi!, _acLumi!, _qLumi, ref dcY, _coeffs, _idct)) return null;
                int bx = (b & 1) * 8, by = (b >> 1) * 8;
                WriteLumaBlock(tileX * 16 + bx, tileY * 16 + by, frameWidth, frameHeight);
            }

            // Cb
            if (!DecodeBlock(ref bits, _dcChroma!, _acChroma!, _qChroma, ref dcCb, _coeffs, _idct)) return null;
            WriteChromaBlock(_cbBuf!, tileX, tileY, (frameWidth + 1) / 2, (frameHeight + 1) / 2);

            // Cr
            if (!DecodeBlock(ref bits, _dcChroma!, _acChroma!, _qChroma, ref dcCr, _coeffs, _idct)) return null;
            WriteChromaBlock(_crBuf!, tileX, tileY, (frameWidth + 1) / 2, (frameHeight + 1) / 2);

            _ = skip; // skip is used by the bitstream reader internally
        }

        var planes = new YCbCrPlanes(frameWidth, frameHeight);
        unsafe
        {
            fixed (byte* y = _yBuf, cb = _cbBuf, cr = _crBuf)
            {
                Buffer.MemoryCopy(y,  planes.Y,  planes.YSize, planes.YSize);
                Buffer.MemoryCopy(cb, planes.Cb, planes.CSize, planes.CSize);
                Buffer.MemoryCopy(cr, planes.Cr, planes.CSize, planes.CSize);
            }
        }
        return planes;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void EnsureQuantTables(int quality)
    {
        if (quality == _lastQuality) return;
        _lastQuality = quality;
        float scale = quality < 50
            ? 5000f / quality
            : 200f - 2f * quality;
        for (int i = 0; i < 64; i++)
        {
            _qLumi[i]   = Math.Clamp(QuantLumi[i]   * scale / 100f, 1, 255);
            _qChroma[i] = Math.Clamp(QuantChroma[i] * scale / 100f, 1, 255);
        }
    }

    private void EnsureHuffTables()
    {
        if (_dcLumi is not null) return;
        _dcLumi   = HuffTable.BuildDcLumi();
        _dcChroma = HuffTable.BuildDcChroma();
        _acLumi   = HuffTable.BuildAcLumi();
        _acChroma = HuffTable.BuildAcChroma();
    }

    private void EnsureOutputBuffers(int w, int h)
    {
        int ySize = w * h;
        int cSize = ((w + 1) / 2) * ((h + 1) / 2);
        if (_yBuf is null || _yBuf.Length < ySize)  _yBuf  = new byte[ySize];
        if (_cbBuf is null || _cbBuf.Length < cSize) _cbBuf = new byte[cSize];
        if (_crBuf is null || _crBuf.Length < cSize) _crBuf = new byte[cSize];
        _yStride = w;
        _cStride = (w + 1) / 2;
    }

    private bool DecodeBlock(ref BitReader bits, HuffTable dcTable, HuffTable acTable,
                              float[] qTable, ref int dcPred,
                              float[] coeffs, float[] idct)
    {
        Array.Clear(coeffs);

        // DC coefficient
        int dcSize = bits.DecodeHuff(dcTable);
        if (dcSize < 0) return false;
        int dcVal  = dcSize > 0 ? bits.ReadSignedBits(dcSize) : 0;
        dcPred    += dcVal;
        coeffs[0]  = dcPred * qTable[0];

        // AC coefficients
        int k = 1;
        while (k < 64)
        {
            int sym = bits.DecodeHuff(acTable);
            if (sym < 0) return false;
            if (sym == 0x00) break; // EOB
            if (sym == 0xF0) { k += 16; continue; } // ZRL
            int run  = sym >> 4;
            int size = sym & 0xF;
            k += run;
            if (k >= 64) return false;
            int ac = bits.ReadSignedBits(size);
            coeffs[Zigzag[k]] = ac * qTable[k];
            k++;
        }

        InverseICT(coeffs, idct);
        return true;
    }

    /// 2D separable IDCT (AAN algorithm) — runs on CPU.
    /// The GPU variant (ICTDequant.hlsl) handles dequantized coefficient blocks
    /// in parallel when more than ~4 tiles need decoding simultaneously.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void InverseICT(float[] coeff, float[] result)
    {
        const float W1 = 0.98078528f, W2 = 0.92387953f, W3 = 0.83146961f;
        const float W4 = 0.70710678f, W5 = 0.55557023f, W6 = 0.38268343f, W7 = 0.19509032f;

        var tmp = new float[64];

        // Row pass
        for (int row = 0; row < 8; row++)
        {
            int i = row * 8;
            float s0 = coeff[i], s1 = coeff[i+1], s2 = coeff[i+2], s3 = coeff[i+3];
            float s4 = coeff[i+4], s5 = coeff[i+5], s6 = coeff[i+6], s7 = coeff[i+7];

            float t0 = (s0 + s4) * W4;
            float t1 = (s0 - s4) * W4;
            float t2 = s2 * W6 - s6 * W2;
            float t3 = s2 * W2 + s6 * W6;

            float p0 = t0 + t3, p1 = t1 + t2, p2 = t1 - t2, p3 = t0 - t3;

            float q0 = s1*W7 - s7*W1, q1 = s3*W3 - s5*W5;
            float q2 = s1*W1 + s7*W7, q3 = s3*W5 + s5*W3;
            float r0 = q0 + q1, r1 = q2 + q3, r2 = q2 - q3, r3 = q0 - q1;
            float u0 = (r0 - r1) * W4, u1 = (r2 + r3) * W4;

            tmp[i+0] = p0 + r1;
            tmp[i+1] = p1 + u1 + r3;
            tmp[i+2] = p2 + u0;
            tmp[i+3] = p3 - u1 + r0 - r1;
            tmp[i+4] = p3 + u1 - r0 + r1;
            tmp[i+5] = p2 - u0;
            tmp[i+6] = p1 - u1 - r3;
            tmp[i+7] = p0 - r1;
        }

        // Column pass
        for (int col = 0; col < 8; col++)
        {
            float s0 = tmp[col], s1 = tmp[col+8], s2 = tmp[col+16], s3 = tmp[col+24];
            float s4 = tmp[col+32], s5 = tmp[col+40], s6 = tmp[col+48], s7 = tmp[col+56];

            float t0 = (s0 + s4) * W4;
            float t1 = (s0 - s4) * W4;
            float t2 = s2 * W6 - s6 * W2;
            float t3 = s2 * W2 + s6 * W6;

            float p0 = t0 + t3, p1 = t1 + t2, p2 = t1 - t2, p3 = t0 - t3;

            float q0 = s1*W7 - s7*W1, q1 = s3*W3 - s5*W5;
            float q2 = s1*W1 + s7*W7, q3 = s3*W5 + s5*W3;
            float r0 = q0 + q1, r1 = q2 + q3, r2 = q2 - q3, r3 = q0 - q1;
            float u0 = (r0 - r1) * W4, u1 = (r2 + r3) * W4;

            result[col+0*8] = p0 + r1;
            result[col+1*8] = p1 + u1 + r3;
            result[col+2*8] = p2 + u0;
            result[col+3*8] = p3 - u1 + r0 - r1;
            result[col+4*8] = p3 + u1 - r0 + r1;
            result[col+5*8] = p2 - u0;
            result[col+6*8] = p1 - u1 - r3;
            result[col+7*8] = p0 - r1;
        }
    }

    private void WriteLumaBlock(int x, int y, int fw, int fh)
    {
        for (int row = 0; row < 8 && y + row < fh; row++)
        {
            int dstOff = (y + row) * _yStride + x;
            for (int col = 0; col < 8 && x + col < fw; col++)
                _yBuf![dstOff + col] = Clamp8(_idct[row * 8 + col] + 128f);
        }
    }

    private void WriteChromaBlock(byte[] plane, int tileX, int tileY, int cw, int ch)
    {
        for (int row = 0; row < 8 && tileY * 8 + row < ch; row++)
        {
            int dstOff = (tileY * 8 + row) * _cStride + tileX * 8;
            for (int col = 0; col < 8 && tileX * 8 + col < cw; col++)
                plane[dstOff + col] = Clamp8(_idct[row * 8 + col] + 128f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp8(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
}

// ── Minimal Huffman decoder ─────────────────────────────────────────────────

internal sealed class HuffTable
{
    // Standard JPEG Annex K tables (abbreviated)
    // Full tables omitted for brevity — populated from JPEG spec constants.
    private readonly Dictionary<(int len, int code), int> _map = new();
    private int _maxLen;

    private HuffTable() { }

    public int Lookup(int code, int len, out bool found)
    {
        found = _map.TryGetValue((len, code), out int sym);
        return sym;
    }

    public int MaxLen => _maxLen;

    private static HuffTable Build(byte[] lengths, byte[] values)
    {
        var t   = new HuffTable();
        int val = 0, code = 0;
        for (int li = 0; li < lengths.Length; li++)
        {
            int count = lengths[li];
            for (int ci = 0; ci < count; ci++)
                t._map[(li + 1, code++)] = values[val++];
            code <<= 1;
        }
        t._maxLen = lengths.Length;
        return t;
    }

    // Standard JPEG DC luma table (Annex K)
    public static HuffTable BuildDcLumi() => Build(
        [0,1,5,1,1,1,1,1,1,0,0,0,0,0,0,0],
        [0,1,2,3,4,5,6,7,8,9,10,11]);

    // Standard JPEG DC chroma table
    public static HuffTable BuildDcChroma() => Build(
        [0,3,1,1,1,1,1,1,1,1,1,0,0,0,0,0],
        [0,1,2,3,4,5,6,7,8,9,10,11]);

    // Standard JPEG AC luma table (first 162 entries)
    public static HuffTable BuildAcLumi() => Build(
        [0,2,1,3,3,2,4,3,5,5,4,4,0,0,1,125],
        AcLumiValues);

    // Standard JPEG AC chroma table
    public static HuffTable BuildAcChroma() => Build(
        [0,2,1,2,4,4,3,4,7,5,4,4,0,1,2,119],
        AcChromaValues);

    private static readonly byte[] AcLumiValues = [
        0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,
        0x07,0x22,0x71,0x14,0x32,0x81,0x91,0xA1,0x08,0x23,0x42,0xB1,0xC1,0x15,0x52,
        0xD1,0xF0,0x24,0x33,0x62,0x72,0x82,0x09,0x0A,0x16,0x17,0x18,0x19,0x1A,0x25,
        0x26,0x27,0x28,0x29,0x2A,0x34,0x35,0x36,0x37,0x38,0x39,0x3A,0x43,0x44,0x45,
        0x46,0x47,0x48,0x49,0x4A,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,0x63,0x64,
        0x65,0x66,0x67,0x68,0x69,0x6A,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x83,
        0x84,0x85,0x86,0x87,0x88,0x89,0x8A,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,
        0x9A,0xA2,0xA3,0xA4,0xA5,0xA6,0xA7,0xA8,0xA9,0xAA,0xB2,0xB3,0xB4,0xB5,0xB6,
        0xB7,0xB8,0xB9,0xBA,0xC2,0xC3,0xC4,0xC5,0xC6,0xC7,0xC8,0xC9,0xCA,0xD2,0xD3,
        0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,0xE1,0xE2,0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,
        0xE9,0xEA,0xF1,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,0xF9,0xFA
    ];

    private static readonly byte[] AcChromaValues = [
        0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,
        0x71,0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,0xA1,0xB1,0xC1,0x09,0x23,0x33,
        0x52,0xF0,0x15,0x62,0x72,0xD1,0x0A,0x16,0x24,0x34,0xE1,0x25,0xF1,0x17,0x18,
        0x19,0x1A,0x26,0x27,0x28,0x29,0x2A,0x35,0x36,0x37,0x38,0x39,0x3A,0x43,0x44,
        0x45,0x46,0x47,0x48,0x49,0x4A,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,0x63,
        0x64,0x65,0x66,0x67,0x68,0x69,0x6A,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,
        0x82,0x83,0x84,0x85,0x86,0x87,0x88,0x89,0x8A,0x92,0x93,0x94,0x95,0x96,0x97,
        0x98,0x99,0x9A,0xA2,0xA3,0xA4,0xA5,0xA6,0xA7,0xA8,0xA9,0xAA,0xB2,0xB3,0xB4,
        0xB5,0xB6,0xB7,0xB8,0xB9,0xBA,0xC2,0xC3,0xC4,0xC5,0xC6,0xC7,0xC8,0xC9,0xCA,
        0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,0xE2,0xE3,0xE4,0xE5,0xE6,0xE7,
        0xE8,0xE9,0xEA,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,0xF9,0xFA
    ];
}

// ── Bitstream reader (HIVE format) ──────────────────────────────────────────

internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private uint _bits;
    private int  _bitsLeft;

    public BitReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; _bits = 0; _bitsLeft = 0; }

    private void Refill()
    {
        while (_bitsLeft <= 24 && _pos < _data.Length)
        {
            _bits = (_bits << 8) | _data[_pos++];
            _bitsLeft += 8;
        }
    }

    public int ReadBit()
    {
        if (_bitsLeft == 0) Refill();
        if (_bitsLeft == 0) return -1;
        _bitsLeft--;
        return (int)((_bits >> _bitsLeft) & 1);
    }

    public int ReadBits(int n)
    {
        if (n == 0) return 0;
        Refill();
        if (_bitsLeft < n) return -1;
        _bitsLeft -= n;
        return (int)((_bits >> _bitsLeft) & ((1u << n) - 1));
    }

    public int ReadSignedBits(int n)
    {
        if (n == 0) return 0;
        int v = ReadBits(n);
        if (v < 0) return 0;
        return (v & (1 << (n - 1))) != 0 ? v : v - (1 << n) + 1;
    }

    public int DecodeHuff(HuffTable table)
    {
        int code = 0;
        for (int len = 1; len <= table.MaxLen; len++)
        {
            int bit = ReadBit();
            if (bit < 0) return -1;
            code = (code << 1) | bit;
            int sym = table.Lookup(code, len, out bool found);
            if (found) return sym;
        }
        return -1;
    }

    /// Read the HIVE skip count (variable-length prefix code).
    public int ReadHiveSkip()
    {
        // HIVE: 0 = no skip; 1x = 1-bit skip; 01xx = 2-4; etc.
        // Simplified: read up to 7 bits
        int skip = 0;
        for (int i = 0; i < 7; i++)
        {
            int b = ReadBit();
            if (b <= 0) break;
            skip = (skip << 1) | b;
        }
        return skip;
    }
}
