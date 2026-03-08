using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinKVM.Framebuffer;

/// Decodes ICT (Integrated Color Transform) encoding — Raritan's custom JPEG-like codec.
/// Direct port of SwiftKVM's ICTDecoder.swift (decompiled from Raritan Java client).
///
/// Key differences from standard JPEG:
///   • Custom Huffman tables (not JPEG Annex K)
///   • Custom quantization (quantFactors[subenc] × normalization coefficients)
///   • HIVE tile-skip bitstream (3-level VLC for inter-frame compression)
///   • Per-tile DC reset (not carried across tiles)
///   • Per-tile byte-count + padding alignment after each tile
///   • Integer IDCT (Int16/Int32 arithmetic, no floating point)
public sealed class ICTDecoder : IDisposable
{
    // ── Quantization tables (same as JPEG baseline) ──────────────────────────

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

    // ── QuantFactors table — indexed by subencoding (ccr) ────────────────────
    // Decompiled from Raritan Java client. subenc=11 → factor=0.5, shift=0.
    private readonly record struct QuantFactor(double Factor, int Shift);
    private static readonly QuantFactor[] QuantFactors = [
        new(0.7,   3), new(0.5,   3), new(0.7,   2), new(0.5,   2),
        new(0.2,   3), new(0.7,   1), new(0.15,  3), new(0.5,   1),
        new(0.2,   2), new(0.7,   0), new(0.15,  2), new(0.5,   0),
        new(0.2,   1), new(0.15,  1), new(0.2,   0), new(0.15,  0),
    ];

    // ── DCT normalization coefficients (baked into quant tables) ─────────────
    private static readonly double[] KCoeff = [
        0.35355333390593, 0.047565149415, 0.158113883008, 0.047565149415,
        0.35355333390593, 0.047565149415, 0.158113883008, 0.047565149415
    ];

    // ── JPEG magnitude decode helpers ─────────────────────────────────────────
    private static readonly int[] Cats = [0, 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047];
    private static readonly int[] Sign = [0, 1, 2, 4,  8, 16, 32,  64, 128, 256,  512, 1024];

    // ── Huffman table entry (flat 65536-entry lookup by 16-bit prefix) ────────
    // BitCount = -1 means uninitialized (sentinel). Valid range is 0..15 (16-bit codes → 0).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HuffEntry
    {
        public sbyte Cat;       // magnitude category (or 12 = end-of-package for DC)
        public sbyte Rl;        // run-length (AC only)
        public sbyte BitCount;  // bits remaining after code (16 - code_length); -1 = unset
    }

    private readonly record struct HuffCode(int Size, int Code);

    // ── Quantization / Huffman instance state ─────────────────────────────────
    private readonly HuffEntry[] _huffDcL;
    private readonly HuffEntry[] _huffDcC;
    private readonly HuffEntry[] _huffAcL;
    private readonly HuffEntry[] _huffAcC;
    private int _currentCcr = -1;
    private readonly int[] _yqTable  = new int[64];
    private readonly int[] _cqTable  = new int[64];

    // ── Coefficient storage for two-pass parallel decode ──────────────────────
    // Phase 1 (serial):  Huffman decode → _allCoeffs
    // Phase 2 (parallel): InverseICT across all decoded blocks → output planes
    // Reused across frames to avoid per-frame allocation.
    private short[] _allCoeffs = [];

    // Job list for phase 2: (coeffOffset, planeId 0=Y/1=Cb/2=Cr, destOffset, linesize)
    private readonly List<(int cOff, int pid, int dOff, int ls)> _idctJobs = [];

    // ── Persistent inter-frame planes (skipped tiles keep previous frame) ─────
    private YCbCrPlanes? _planes;

    // ── Bit-reader state ──────────────────────────────────────────────────────
    private ulong  _bitBuf;
    private int    _bitCount;
    private byte[] _streamBuf = [];
    private int    _streamLen;
    private int    _streamOff;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ICTDecoder()
    {
        _huffDcL = BuildHuffTable(LumDcCodes);
        _huffDcC = BuildHuffTable(ChromaDcCodes);
        _huffAcL = BuildHuffTable(LumAcCodes);
        _huffAcC = BuildHuffTable(ChromaAcCodes);
    }

    public void Dispose()
    {
        _planes?.Dispose();
        _planes = null;
    }

    public void ReleaseBuffers()
    {
        _planes?.Dispose();
        _planes = null;
    }

    // ── Huffman table construction ────────────────────────────────────────────

    private static HuffEntry[] BuildHuffTable(HuffCode[] codes)
    {
        // Initialize all entries as unset (BitCount = -1 is the sentinel).
        var tab = new HuffEntry[65536];
        for (int i = 0; i < tab.Length; i++) tab[i].BitCount = -1;

        for (int i = 0; i < codes.Length; i++)
        {
            int size = codes[i].Size;
            if (size == 0) continue;
            int idx = (codes[i].Code << (16 - size)) & 0xFFFF;
            tab[idx].Cat      = (sbyte)(i & 0xF);
            tab[idx].Rl       = (sbyte)((i >> 4) & 0xF);
            tab[idx].BitCount = (sbyte)(16 - size); // 0 for 16-bit codes — valid, not sentinel
        }

        // Fill unoccupied entries by propagating the last explicitly-set entry.
        // Uses BitCount == -1 as sentinel, since valid codes have BitCount 0..15.
        sbyte lastCat = 0, lastRl = 0, lastBc = 0;
        for (int i = 0; i < 65536; i++)
        {
            ref var e = ref tab[i];
            if (e.BitCount != -1) { lastCat = e.Cat; lastRl = e.Rl; lastBc = e.BitCount; }
            else                  { e.Cat = lastCat; e.Rl = lastRl; e.BitCount = lastBc;  }
        }
        return tab;
    }

    // ── Quantization table setup ──────────────────────────────────────────────

    private void SetupQuantTables(int ccr)
    {
        const int scaler = 16; // scalerShift(6) + 10
        var mul = QuantFactors[ccr];
        for (int i = 0; i < 8; i++)
        for (int j = 0; j < 8; j++)
        {
            int pos = i * 8 + j;
            double n = KCoeff[i] * KCoeff[j];
            _yqTable[pos] = QuantTableValue(QuantLumi[pos],   mul.Factor, mul.Shift, scaler, n);
            _cqTable[pos] = QuantTableValue(QuantChroma[pos], mul.Factor, mul.Shift, scaler, n);
        }
    }

    private static int QuantTableValue(int b, double factor, int fShift, int scaler, double norm)
    {
        int entry = (int)(8192.0 / (b * factor) + 0.5);
        if (entry == 8192) entry--;
        entry >>= fShift;
        return (int)(8192.0 / entry * (1 << scaler) * norm + 0.5);
    }

    // ── Bit reader ────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureBits(int n)
    {
        while (_bitCount < n)
        {
            ulong b = _streamOff < _streamLen ? _streamBuf[_streamOff] : 0u;
            _streamOff++;
            _bitBuf |= b << (56 - _bitCount);
            _bitCount += 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PeekBits(int n) => (int)(_bitBuf >> (64 - n));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DropBits(int n) { _bitBuf <<= n; _bitCount -= n; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetBits(int n)
    {
        int v = (int)(_bitBuf >> (64 - n));
        _bitBuf <<= n; _bitCount -= n;
        return v;
    }

    // ── HIVE tile-skip VLC ────────────────────────────────────────────────────
    // Format:
    //   0            → skip = 0 (decode this tile)
    //   1 0 <6-bit>  → skip = <6-bit value>  (0..63)
    //   1 1 <16-bit> → skip = <16-bit value>; 65535 = end-of-update sentinel (-1)

    private int ReadHiveSkip()
    {
        EnsureBits(1);
        if (GetBits(1) == 0) return 0;
        EnsureBits(1);
        if (GetBits(1) == 0) { EnsureBits(6); return GetBits(6); }
        EnsureBits(16);
        int v = GetBits(16);
        return v == 65535 ? -1 : v;
    }

    // After each decoded tile: read the byte-count field (ignored, used by
    // hardware for random-access seeking) then pad to byte boundary.

    private void ReadHiveTileByteCount()
    {
        EnsureBits(1);
        if (GetBits(1) == 0) { EnsureBits(7);  GetBits(7);  }
        else                 { EnsureBits(10); GetBits(10); }
    }

    private void ReadHiveTilePadding()
    {
        int pad = _bitCount % 8;
        if (pad > 0) DropBits(pad);
    }

    // ── JPEG magnitude decoder ────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetRealValue(int cat)
    {
        if (cat == 0) return 0;
        EnsureBits(cat);
        int bits = GetBits(cat);
        return (bits & Sign[cat]) != 0 ? bits : -(Cats[cat] - bits);
    }

    // ── Huffman block decode → coefficients in blockBuf[off..off+64] ─────────

    // DecodeBlockInto writes into a caller-supplied array at offset `off`.
    // Used in Phase 1 (serial) — thread-safe since each block has a unique offset.
    private int DecodeBlockInto(short[] buf, int off, int lastDc, int[] qTable, HuffEntry[] dcTab, HuffEntry[] acTab)
    {
        EnsureBits(16);
        int dcIdx = PeekBits(16);
        int cat = dcTab[dcIdx].Cat;
        DropBits(16 - dcTab[dcIdx].BitCount);
        if (cat == 12) return lastDc; // end-of-package sentinel

        int dc = lastDc + GetRealValue(cat);
        buf[off] = (short)((int)((long)dc * qTable[0]) >> 10);

        int count = 0;
        while (count < 64)
        {
            EnsureBits(16);
            int acIdx = PeekBits(16);
            int acCat = acTab[acIdx].Cat;
            int rl    = acTab[acIdx].Rl;
            DropBits(16 - acTab[acIdx].BitCount);
            if (rl == 0 && acCat == 0) break; // EOB

            int value = GetRealValue(acCat);
            count += rl + 1;
            if (count < 64)
            {
                int t = Zigzag[count];
                buf[t + off] = (short)((int)((long)qTable[t] * value) >> 10);
            }
        }
        return dc;
    }

    // ── Integer Inverse ICT (port of SwiftKVM's inverseICT) ──────────────────
    // Reads 8×8 Int16 block from blockBuf[tileOff..], writes bytes to dest.
    // Final output: saturate((value >> 6) + 128) → UInt8.

    // Static + stackalloc scratch → fully thread-safe for Parallel.For.
    private static unsafe void InverseICT(short[] src, int tileOff, byte* dest, int destOff, int linesize)
    {
        // Row pass — write into stack-allocated scratch (thread-local, no shared state)
        Span<short> scratch = stackalloc short[64];

        for (int row = 0; row < 8; row++)
        {
            int off = tileOff + 8 * row;
            int x0 = src[off], x1 = src[off+1], x2 = src[off+2], x3 = src[off+3];
            int x4 = src[off+4], x5 = src[off+5], x6 = src[off+6], x7 = src[off+7];

            if ((x1 | x2 | x3 | x4 | x5 | x6 | x7) == 0)
            {
                short s = src[off];
                int sr = row * 8;
                scratch[sr]=scratch[sr+1]=scratch[sr+2]=scratch[sr+3]=
                scratch[sr+4]=scratch[sr+5]=scratch[sr+6]=scratch[sr+7]=s;
                continue;
            }

            int a0 = x0+x6, a1 = x0-x6, a2 = x2+x4, a3 = x2-x4;
            int x2_2 = x2<<1, x6_2 = x6<<1;
            int b0 = a0+a2+x2_2, b2 = a1-x6_2+a3, b4 = a0+x6_2-a2, b6 = a1-(a3+x2_2);

            int d0 = x1+x7, d1 = x1-x7, d2 = x5+x3, d3 = x5-x3;
            int e0 = d0-d3, e1 = d0-d2, e2 = d3-d1, e3 = d2+d1;
            int f0 = -8*x1+x3, f1 = x1+8*x5, f2 = x7+8*x3, f3 = x5+8*x7;
            int bo0 = 2*e0+8*d2-f0, bo1 = 2*e1+8*d1-f1, bo2 = 2*e2+8*d0-f2, bo3 = 2*e3+8*d3-f3;

            int sr2 = row * 8;
            scratch[sr2+0] = (short)(b0+bo0); scratch[sr2+7] = (short)(b0-bo0);
            scratch[sr2+1] = (short)(b2+bo1); scratch[sr2+6] = (short)(b2-bo1);
            scratch[sr2+2] = (short)(b4+bo2); scratch[sr2+5] = (short)(b4-bo2);
            scratch[sr2+3] = (short)(b6+bo3); scratch[sr2+4] = (short)(b6-bo3);
        }

        // Column pass — reads from scratch (stack-local), writes bytes to dest
        for (int col = 0; col < 8; col++)
        {
            int x0 = scratch[col],    x1 = scratch[col+8],  x2 = scratch[col+16], x3 = scratch[col+24];
            int x4 = scratch[col+32], x5 = scratch[col+40], x6 = scratch[col+48], x7 = scratch[col+56];

            if ((x1 | x2 | x3 | x4 | x5 | x6 | x7) == 0)
            {
                byte val = Sat(x0);
                int d = destOff + col;
                for (int r = 0; r < 8; r++) { dest[d] = val; d += linesize; }
                continue;
            }

            int a0 = x0+x6, a1 = x0-x6, a2 = x2+x4, a3 = x2-x4;
            int x2_2 = x2<<1, x6_2 = x6<<1;
            int b0 = a0+a2+x2_2, b2 = a1-x6_2+a3, b4 = a0+x6_2-a2, b6 = a1-(a3+x2_2);

            int d0 = x1+x7, d1 = x1-x7, d2 = x5+x3, d3 = x5-x3;
            int e0 = d0-d3, e1 = d0-d2, e2 = d3-d1, e3 = d2+d1;
            int f0 = -8*x1+x3, f1 = x1+8*x5, f2 = x7+8*x3, f3 = x5+8*x7;
            int bo0 = 2*e0+8*d2-f0, bo1 = 2*e1+8*d1-f1, bo2 = 2*e2+8*d0-f2, bo3 = 2*e3+8*d3-f3;

            int doff = destOff + col;
            dest[doff]=Sat(b0+bo0); doff+=linesize;
            dest[doff]=Sat(b2+bo1); doff+=linesize;
            dest[doff]=Sat(b4+bo2); doff+=linesize;
            dest[doff]=Sat(b6+bo3); doff+=linesize;
            dest[doff]=Sat(b6-bo3); doff+=linesize;
            dest[doff]=Sat(b4-bo2); doff+=linesize;
            dest[doff]=Sat(b2-bo1); doff+=linesize;
            dest[doff]=Sat(b0-bo0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Sat(int v) => (byte)Math.Clamp((v >> 6) + 128, 0, 255);

    // ── Main decode entry point ───────────────────────────────────────────────
    // Returns the persistent internal YCbCrPlanes (do NOT Dispose the return value).
    // Returns null only if the frame size is invalid.

    public YCbCrPlanes? Decode(ReadOnlySpan<byte> data, int w, int h, int subenc)
    {
        if (w <= 0 || h <= 0 || (w % 16) != 0 || (h % 16) != 0) return null;

        int ccr = subenc & 0xF;
        if (ccr != _currentCcr) { _currentCcr = ccr; SetupQuantTables(ccr); }

        if (_planes is null || _planes.Width != w || _planes.Height != h)
        {
            _planes?.Dispose();
            _planes = new YCbCrPlanes(w, h);
        }

        if (_streamBuf.Length < data.Length) _streamBuf = new byte[data.Length];
        data.CopyTo(_streamBuf);
        _streamLen = data.Length;
        _streamOff = 0; _bitBuf = 0; _bitCount = 0;

        int tilesX  = w / 16;
        int yStride = w;
        int cStride = w / 2;
        int noTiles = tilesX * (h / 16);

        // Ensure coefficient buffer is large enough for all blocks this frame.
        int needed = noTiles * 6 * 64;
        if (_allCoeffs.Length < needed) _allCoeffs = new short[needed];

        _idctJobs.Clear();
        _idctJobs.Capacity = Math.Max(_idctJobs.Capacity, noTiles * 6);

        // ── Phase 1: serial Huffman decode → coefficient buffer + job list ──────
        unsafe
        {
            // _planes uses NativeMemory (unmanaged) — pointers are GC-stable, no fixed needed.
            byte* pY  = _planes.Y;
            byte* pCb = _planes.Cb;
            byte* pCr = _planes.Cr;

            {
                int lineOffsetY = 16 * yStride - w;
                int lineOffsetC = 8 * cStride - (w / 2);
                int offsetY12 = 0, offsetY34 = 8 * yStride, offsetC = 0;
                int lineCounter = 0, tileIdx = 0;

                int skipCount = ReadHiveSkip();

                for (int ty = 0; ty < h; ty += 16)
                for (int tx = 0; tx < w; tx += 16)
                {
                    if (skipCount < 0) goto done;

                    int lastDcY = 0, lastDcCb = 0, lastDcCr = 0;

                    if (skipCount > 0)
                    {
                        if (lineCounter == tilesX) { lineCounter = 0; offsetY12 += lineOffsetY; offsetY34 += lineOffsetY; offsetC += lineOffsetC; }
                        offsetY12 += 16; offsetY34 += 16; offsetC += 8;
                        lineCounter++; skipCount--;
                    }
                    else
                    {
                        ReadHiveTileByteCount();

                        for (int color = 0; color <= 5; color++)
                        {
                            int cOff = (tileIdx * 6 + color) * 64;
                            Array.Clear(_allCoeffs, cOff, 64);

                            if (lineCounter == tilesX) { lineCounter = 0; offsetY12 += lineOffsetY; offsetY34 += lineOffsetY; offsetC += lineOffsetC; }

                            if (color <= 3)
                            {
                                int dOff;
                                if (color <= 1) { dOff = offsetY12; offsetY12 += 8; }
                                else            { dOff = offsetY34; offsetY34 += 8; }
                                lastDcY = DecodeBlockInto(_allCoeffs, cOff, lastDcY, _yqTable, _huffDcL, _huffAcL);
                                _idctJobs.Add((cOff, 0, dOff, yStride));
                            }
                            else if (color == 4)
                            {
                                lastDcCb = DecodeBlockInto(_allCoeffs, cOff, lastDcCb, _cqTable, _huffDcC, _huffAcC);
                                _idctJobs.Add((cOff, 1, offsetC, cStride));
                            }
                            else
                            {
                                lastDcCr = DecodeBlockInto(_allCoeffs, cOff, lastDcCr, _cqTable, _huffDcC, _huffAcC);
                                _idctJobs.Add((cOff, 2, offsetC, cStride));
                                offsetC += 8;
                            }
                        }

                        lineCounter++; tileIdx++;
                        ReadHiveTilePadding();
                        skipCount = ReadHiveSkip();
                    }
                }
                done:;

                // ── Phase 2: parallel IDCT across all Snapdragon cores ─────────
                // Threshold of 48 blocks (~8 tiles) amortises thread-pool overhead.
                // Below that, sequential is faster (matches SwiftKVM's heuristic).
                short[] coeffs = _allCoeffs;
                var jobs = _idctJobs;

                if (jobs.Count >= 48)
                {
                    System.Threading.Tasks.Parallel.For(0, jobs.Count, i =>
                    {
                        var (co, pid, dOff, ls) = jobs[i];
                        byte* dest = pid == 0 ? pY : pid == 1 ? pCb : pCr;
                        InverseICT(coeffs, co, dest, dOff, ls);
                    });
                }
                else
                {
                    foreach (var (co, pid, dOff, ls) in jobs)
                    {
                        byte* dest = pid == 0 ? pY : pid == 1 ? pCb : pCr;
                        InverseICT(coeffs, co, dest, dOff, ls);
                    }
                }
            }
        }

        return _planes;
    }

    // ── Huffman code tables (decompiled from Raritan Java client by SwiftKVM) ─

    private static readonly HuffCode[] LumDcCodes = [
        new(2,  0), new(3,   2), new(3,    3), new(3,   4), new(3,    5), new(3,   6),
        new(4, 14), new(5,  30), new(6,   62), new(7, 126), new(8,  254), new(9, 510),
        new(10, 1022)
    ];

    private static readonly HuffCode[] ChromaDcCodes = [
        new(2, 0), new(2,  1), new(2,   2), new(3,   6), new(4,  14), new(5,  30),
        new(6, 62), new(7, 126), new(8, 254), new(9, 510), new(10, 1022), new(11, 2046)
    ];

    private static readonly HuffCode[] LumAcCodes = [
        // Row 0 (run=0, sizes 0..10) + padding to 16
        new(4,10), new(2,0), new(2,1), new(3,4), new(4,11), new(5,26), new(7,120), new(8,248),
        new(10,1014), new(16,65410), new(16,65411),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 1 (run=1)
        new(0,0), new(4,12), new(5,27), new(7,121), new(9,502), new(11,2038),
        new(16,65412), new(16,65413), new(16,65414), new(16,65415), new(16,65416),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 2 (run=2)
        new(0,0), new(5,28), new(8,249), new(10,1015), new(12,4084),
        new(16,65417), new(16,65418), new(16,65419), new(16,65420), new(16,65421), new(16,65422),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 3 (run=3)
        new(0,0), new(6,58), new(9,503), new(12,4085),
        new(16,65423), new(16,65424), new(16,65425), new(16,65426), new(16,65427), new(16,65428), new(16,65429),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 4 (run=4)
        new(0,0), new(6,59), new(10,1016),
        new(16,65430), new(16,65431), new(16,65432), new(16,65433), new(16,65434), new(16,65435), new(16,65436), new(16,65437),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 5 (run=5)
        new(0,0), new(7,122), new(11,2039),
        new(16,65438), new(16,65439), new(16,65440), new(16,65441), new(16,65442), new(16,65443), new(16,65444), new(16,65445),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 6 (run=6)
        new(0,0), new(7,123), new(12,4086),
        new(16,65446), new(16,65447), new(16,65448), new(16,65449), new(16,65450), new(16,65451), new(16,65452), new(16,65453),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 7 (run=7)
        new(0,0), new(8,250), new(12,4087),
        new(16,65454), new(16,65455), new(16,65456), new(16,65457), new(16,65458), new(16,65459), new(16,65460), new(16,65461),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 8 (run=8)
        new(0,0), new(9,504), new(15,32704),
        new(16,65462), new(16,65463), new(16,65464), new(16,65465), new(16,65466), new(16,65467), new(16,65468), new(16,65469),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 9 (run=9)
        new(0,0), new(9,505),
        new(16,65470), new(16,65471), new(16,65472), new(16,65473), new(16,65474), new(16,65475), new(16,65476), new(16,65477), new(16,65478),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 10 (run=10)
        new(0,0), new(9,506),
        new(16,65479), new(16,65480), new(16,65481), new(16,65482), new(16,65483), new(16,65484), new(16,65485), new(16,65486), new(16,65487),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 11 (run=11)
        new(0,0), new(10,1017),
        new(16,65488), new(16,65489), new(16,65490), new(16,65491), new(16,65492), new(16,65493), new(16,65494), new(16,65495), new(16,65496),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 12 (run=12)
        new(0,0), new(10,1018),
        new(16,65497), new(16,65498), new(16,65499), new(16,65500), new(16,65501), new(16,65502), new(16,65503), new(16,65504), new(16,65505),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 13 (run=13)
        new(0,0), new(11,2040),
        new(16,65506), new(16,65507), new(16,65508), new(16,65509), new(16,65510), new(16,65511), new(16,65512), new(16,65513), new(16,65514),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 14 (run=14)
        new(0,0), new(16,65515), new(16,65516), new(16,65517), new(16,65518), new(16,65519),
        new(16,65520), new(16,65521), new(16,65522), new(16,65523), new(16,65524),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 15 (run=15)
        new(11,2041),
        new(16,65525), new(16,65526), new(16,65527), new(16,65528), new(16,65529),
        new(16,65530), new(16,65531), new(16,65532), new(16,65533), new(16,65534),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
    ];

    private static readonly HuffCode[] ChromaAcCodes = [
        // Row 0 (run=0)
        new(2,0), new(2,1), new(3,4), new(4,10), new(5,24), new(5,25), new(6,56), new(7,120),
        new(9,500), new(10,1014), new(12,4084),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 1 (run=1)
        new(0,0), new(4,11), new(6,57), new(8,246), new(9,501), new(11,2038), new(12,4085),
        new(16,65416), new(16,65417), new(16,65418), new(16,65419),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 2 (run=2)
        new(0,0), new(5,26), new(8,247), new(10,1015), new(12,4086), new(15,32706),
        new(16,65420), new(16,65421), new(16,65422), new(16,65423), new(16,65424),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 3 (run=3)
        new(0,0), new(5,27), new(8,248), new(10,1016), new(12,4087),
        new(16,65425), new(16,65426), new(16,65427), new(16,65428), new(16,65429), new(16,65430),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 4 (run=4)
        new(0,0), new(6,58), new(9,502),
        new(16,65431), new(16,65432), new(16,65433), new(16,65434), new(16,65435), new(16,65436), new(16,65437), new(16,65438),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 5 (run=5)
        new(0,0), new(6,59), new(10,1017),
        new(16,65439), new(16,65440), new(16,65441), new(16,65442), new(16,65443), new(16,65444), new(16,65445), new(16,65446),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 6 (run=6)
        new(0,0), new(7,121), new(11,2039),
        new(16,65447), new(16,65448), new(16,65449), new(16,65450), new(16,65451), new(16,65452), new(16,65453), new(16,65454),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 7 (run=7)
        new(0,0), new(7,122), new(11,2040),
        new(16,65455), new(16,65456), new(16,65457), new(16,65458), new(16,65459), new(16,65460), new(16,65461), new(16,65462),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 8 (run=8)
        new(0,0), new(8,249),
        new(16,65463), new(16,65464), new(16,65465), new(16,65466), new(16,65467), new(16,65468), new(16,65469), new(16,65470), new(16,65471),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 9 (run=9)
        new(0,0), new(9,503),
        new(16,65472), new(16,65473), new(16,65474), new(16,65475), new(16,65476), new(16,65477), new(16,65478), new(16,65479), new(16,65480),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 10 (run=10)
        new(0,0), new(9,504),
        new(16,65481), new(16,65482), new(16,65483), new(16,65484), new(16,65485), new(16,65486), new(16,65487), new(16,65488), new(16,65489),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 11 (run=11)
        new(0,0), new(9,505),
        new(16,65490), new(16,65491), new(16,65492), new(16,65493), new(16,65494), new(16,65495), new(16,65496), new(16,65497), new(16,65498),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 12 (run=12)
        new(0,0), new(9,506),
        new(16,65499), new(16,65500), new(16,65501), new(16,65502), new(16,65503), new(16,65504), new(16,65505), new(16,65506), new(16,65507),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 13 (run=13)
        new(0,0), new(11,2041),
        new(16,65508), new(16,65509), new(16,65510), new(16,65511), new(16,65512), new(16,65513), new(16,65514), new(16,65515), new(16,65516),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 14 (run=14)
        new(0,0), new(14,16352),
        new(16,65517), new(16,65518), new(16,65519), new(16,65520), new(16,65521), new(16,65522), new(16,65523), new(16,65524), new(16,65525),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
        // Row 15 (run=15)
        new(10,1018), new(15,32707),
        new(16,65526), new(16,65527), new(16,65528), new(16,65529), new(16,65530), new(16,65531), new(16,65532), new(16,65533), new(16,65534),
        new(0,0), new(0,0), new(0,0), new(0,0), new(0,0),
    ];
}
