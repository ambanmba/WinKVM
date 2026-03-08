using System.Buffers.Binary;
using System.Text;

namespace WinKVM.Protocol;

// ── Server → Client message types ──────────────────────────────────────────
public enum ServerMessage : byte
{
    FramebufferUpdate       = 0,
    SetColourMapEntries     = 1,
    Bell                    = 2,
    UserNotification        = 3,
    PortList                = 4,
    ServerInit              = 5,
    Utf8String              = 7,
    VideoSettingsS2C        = 8,
    KeyboardLayout          = 9,
    OsdState                = 16,
    VideoQualityS2C         = 17,
    ConnectionParameterList = 18,
    AckPixelFormat          = 19,
    AuthCaps                = 32,   // 0x20
    SessionChallenge        = 33,   // 0x21
    AuthSuccessful          = 34,   // 0x22
    ServerFBFormat          = 128,  // 0x80
    ServerRCMessage         = 131,
    ServerCommand           = 132,
    PingRequest             = 148,  // 0x94
    PingReply               = 149,  // 0x95
    BandwidthRequest        = 150,
    MouseCapsResponse       = 165,
    VmMountsResponse        = 166,
    VmShareTable            = 167,
    VirtualMediaConfig      = 168,  // 0xA8
    UsbProfileList          = 170,  // 0xAA
    RawFramebufferData      = 171,  // 0xAB
    PortListExt             = 172,  // 0xAC
    TerminalConfig          = 173,  // 0xAD
    TerminalInputSettings   = 174,  // 0xAE
}

// ── Client → Server message types ──────────────────────────────────────────
public enum ClientMessage : byte
{
    SetPixelFormat          = 0,
    SetEncodings            = 2,
    FramebufferUpdateRequest = 3,
    KeyEvent                = 4,
    PointerEvent            = 5,
    ClientInit              = 7,
    Login                   = 32,   // 0x20
    ChallengeResponse       = 33,   // 0x21
    KvmSwitchEvent          = 137,  // 0x89
    PingRequest             = 148,  // 0x94
    PingReply               = 149,  // 0x95
}

// ── RFB encoding types ──────────────────────────────────────────────────────
public enum RfbEncoding : int
{
    Raw      = 0,
    CopyRect = 1,
    Rre      = 2,
    CoRRE    = 4,
    Hextile  = 5,
    Zlib     = 6,
    Tight    = 7,
    NewFBSize = -223,  // 0xFFFFFF21
    LastRect  = -224,
}

// ── Hextile sub-encoding flags ──────────────────────────────────────────────
public static class HextileFlags
{
    public const byte Raw                = 1;
    public const byte BackgroundSpecified = 2;
    public const byte ForegroundSpecified = 4;
    public const byte AnySubrects        = 8;
    public const byte SubrectsColoured   = 16;
}

// ── Auth method flags ───────────────────────────────────────────────────────
public static class AuthMethod
{
    public const byte HttpSessionId = 1;
    public const byte Plain         = 2;
    public const byte Md5           = 4;
    public const byte NoAuth        = 8;
}

// ── Pixel format ────────────────────────────────────────────────────────────
public struct PixelFormat
{
    public byte   BitsPerPixel;
    public byte   Depth;
    public bool   BigEndian;
    public bool   TrueColour;
    public ushort RedMax;
    public ushort GreenMax;
    public ushort BlueMax;
    public byte   RedShift;
    public byte   GreenShift;
    public byte   BlueShift;

    /// 32bpp BGRX little-endian — native Direct3D BGRA layout, no swizzle needed.
    public static readonly PixelFormat Bgrx32 = new()
    {
        BitsPerPixel = 32, Depth = 24,
        BigEndian = false, TrueColour = true,
        RedMax = 255, GreenMax = 255, BlueMax = 255,
        RedShift = 16, GreenShift = 8, BlueShift = 0
    };
}

// ── Framebuffer update header ───────────────────────────────────────────────
public readonly record struct FBUpdateHeader(byte Flags, ushort NumRects, uint Size);

// ── Rectangle header (V01_27 format: 16 bytes) ─────────────────────────────
public readonly record struct RectHeader(ushort X, ushort Y, ushort Width, ushort Height, int Encoding, uint DataSize);

// ── Session state ───────────────────────────────────────────────────────────
public enum SessionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
}

// ── KVM port ────────────────────────────────────────────────────────────────
public record KvmPort(string Id, ushort Number, string Name);

// ── USB profile ─────────────────────────────────────────────────────────────
public record UsbProfile(ushort Id, string Name);

// ── Video settings ──────────────────────────────────────────────────────────
public record VideoSettings(string Mode, int Width, int Height);

// ── Binary writer (big-endian, matching e-RIC protocol) ────────────────────
public sealed class BinaryWriter2
{
    private readonly List<byte> _data = new();

    public byte[] ToArray() => _data.ToArray();
    public int Count => _data.Count;

    public void WriteU8(byte v)   => _data.Add(v);
    public void WriteU16(ushort v) { _data.Add((byte)(v >> 8)); _data.Add((byte)v); }
    public void WriteU32(uint v)   { _data.Add((byte)(v >> 24)); _data.Add((byte)(v >> 16)); _data.Add((byte)(v >> 8)); _data.Add((byte)v); }
    public void WriteI32(int v)    => WriteU32((uint)v);
    public void WriteBytes(ReadOnlySpan<byte> bytes) => _data.AddRange(bytes.ToArray());
    public void WriteString(string s) { _data.AddRange(Encoding.UTF8.GetBytes(s)); _data.Add(0); }
}

// ── Binary reader (big-endian) ──────────────────────────────────────────────
public ref struct BinaryReader2
{
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;
    public bool Overflowed { get; private set; }
    public int Remaining => _data.Length - _offset;

    public BinaryReader2(ReadOnlySpan<byte> data) { _data = data; _offset = 0; Overflowed = false; }

    public byte ReadU8()
    {
        if (_offset + 1 > _data.Length) { Overflowed = true; return 0; }
        return _data[_offset++];
    }

    public ushort ReadU16()
    {
        if (_offset + 2 > _data.Length) { Overflowed = true; return 0; }
        var v = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_offset, 2));
        _offset += 2;
        return v;
    }

    public uint ReadU32()
    {
        if (_offset + 4 > _data.Length) { Overflowed = true; return 0; }
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_offset, 4));
        _offset += 4;
        return v;
    }

    public int ReadI32() => (int)ReadU32();

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || _offset + count > _data.Length) { Overflowed = true; return ReadOnlySpan<byte>.Empty; }
        var slice = _data.Slice(_offset, count);
        _offset += count;
        return slice;
    }

    public void Skip(int count) => _offset = Math.Min(_offset + count, _data.Length);
}

// ── Chunk reader ────────────────────────────────────────────────────────────
/// Reads data transparently across chunk boundaries for chunk-wise FBUpdate.
/// Each chunk: flags(1) + size(3, big-endian 24-bit). Bit 0 of flags = is_last.
public sealed class ChunkReader
{
    private const int MaxChunkSize   = 16 * 1024 * 1024;  // 16MB
    private const int MaxReadAllSize = 256 * 1024 * 1024; // 256MB

    private readonly ERICConnection _conn;
    private int  _available;
    private bool _isLast;
    private bool _finished;

    public ChunkReader(ERICConnection conn) => _conn = conn;

    public async Task<byte[]> ReadAsync(int count, CancellationToken ct = default)
    {
        var result = new byte[count];
        int written = 0;
        while (written < count)
        {
            if (_available <= 0 && !_isLast)
                await ReadChunkHeaderAsync(ct);

            if (_available <= 0)
                throw new IOException($"ChunkReader: premature end, still needed {count - written} bytes");

            int toRead = Math.Min(count - written, _available);
            var slice = await _conn.ReadAsync(toRead, ct);
            slice.CopyTo(result, written);
            written    += toRead;
            _available -= toRead;
        }
        return result;
    }

    public async Task<byte[]> ReadAllAsync(int sizeHint = 0, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(sizeHint > 0 ? sizeHint : 4096);
        if (_available > 0)
        {
            var data = await _conn.ReadAsync(_available, ct);
            ms.Write(data);
            _available = 0;
        }
        while (!_isLast)
        {
            await ReadChunkHeaderAsync(ct);
            if (_available > 0)
            {
                if (ms.Length + _available > MaxReadAllSize)
                    throw new IOException("ChunkReader: exceeded 256MB limit");
                var data = await _conn.ReadAsync(_available, ct);
                ms.Write(data);
                _available = 0;
            }
        }
        return ms.ToArray();
    }

    public async Task FinishAsync(CancellationToken ct = default)
    {
        if (_finished) return;
        _finished = true;
        while (_available > 0 || !_isLast)
        {
            if (_available > 0) { await _conn.ReadAsync(_available, ct); _available = 0; }
            if (!_isLast) await ReadChunkHeaderAsync(ct);
        }
    }

    private async Task ReadChunkHeaderAsync(CancellationToken ct)
    {
        var header = await _conn.ReadAsync(4, ct);
        _isLast    = (header[0] & 1) != 0;
        _available = (header[1] << 16) | (header[2] << 8) | header[3];
        if (_available > MaxChunkSize)
            throw new IOException($"ChunkReader: chunk size {_available} exceeds limit");
    }
}
