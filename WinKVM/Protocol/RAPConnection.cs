using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace WinKVM.Protocol;

/// RAP (Remote Audio Protocol) connection — receives PCM audio from a Raritan KVM.
///
/// Protocol flow:
///   1. TLS connect to same host:port as e-RIC
///   2. Handshake: send "e-RIC RAP P", read 16-byte version, echo it back
///   3. Auth: send type-4 (username/password) packet, read 8-byte response
///   4. Connection request: type-0x07, request speaker 44100/16/stereo
///   5. Receive loop: dispatch type-0x03 audio packets to callback
///
/// Mirrors SwiftKVM's RAPProbe.swift.
public sealed class RAPConnection : IDisposable
{
    // ── Message types ─────────────────────────────────────────────────────────
    private const byte MsgPing       = 0x00;
    private const byte MsgPong       = 0x01;
    private const byte MsgNotif      = 0x02;
    private const byte MsgAudioData  = 0x03;
    private const byte MsgAuthPwd    = 0x04;
    private const byte MsgAuthSess   = 0x05;
    private const byte MsgConnReq    = 0x07;
    private const byte MsgResponse   = 0x80;
    private const byte MsgBlockSize  = 0x81;

    // Device types
    private const byte DeviceSpeaker = 0x01;
    private const byte DeviceMic     = 0x02;

    // Encoding
    private const byte EncUnsigned = 0x00; // 8-bit PCM unsigned
    private const byte EncSigned   = 0x01; // 16-bit PCM signed

    // Auth success: reason = 0x92020000
    private const uint AuthSuccessReason = 0x92020000;

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly string  _host;
    private readonly int     _port;
    private readonly string  _username;
    private readonly string  _password;
    private readonly uint    _rfbSessionId;   // from ServerInit (0x05)
    private readonly string? _ericSessionId;  // from ConnectionParams, used for type-5 auth

    private TcpClient?  _tcp;
    private SslStream?  _ssl;
    private bool        _disposed;

    private static readonly string _log = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "winkvm_rap.log");

    /// Called for each received audio packet.
    /// Parameters: (pcmData, sampleRate, channels, bitsPerSample, isSigned)
    public event Action<byte[], int, int, int, bool>? AudioPacketReceived;

    public RAPConnection(string host, int port, string username, string password,
                         uint rfbSessionId = 0, string? ericSessionId = null)
    {
        _host = host; _port = port;
        _username = username; _password = password;
        _rfbSessionId = rfbSessionId;
        _ericSessionId = ericSessionId;
    }

    // ── Connect + auth ────────────────────────────────────────────────────────

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        Log($"RAP ConnectAsync {_host}:{_port}");
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(_host, _port, ct);

            _ssl = new SslStream(_tcp.GetStream(), false, (_, _, _, _) => true);
            await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);

            // ── Handshake ─────────────────────────────────────────────────────
            // Client sends: "e-RIC RAP P" (11 bytes ASCII)
            await _ssl.WriteAsync("e-RIC RAP P"u8.ToArray(), ct);

            // Server responds: "e-RIC RAP xx.xx\n" (16 bytes)
            var version = await ReadExactAsync(16, ct);
            Log($"RAP version: '{Encoding.ASCII.GetString(version).TrimEnd()}'");

            // Client echoes version back
            await _ssl.WriteAsync(version, ct);

            // ── Auth: try session-ID (type 5) first, fall back to password (type 4) ──
            bool authOk;
            if (!string.IsNullOrEmpty(_ericSessionId))
                authOk = await AuthWithSessionIdAsync(ct);
            else
                authOk = await AuthWithPasswordAsync(ct);

            if (!authOk) return false;

            // ── Connection request (type 0x07) ────────────────────────────────
            // Request speaker, 16-bit signed, stereo, 44100 Hz
            // [0x07][count=1][ms_idx=0xFF][reserved=0][rfbSessionId(4 BE)]
            // [deviceType][encoding][channels][bits][sampleRate(4 BE)]
            int  sampleRate = 44100;
            byte channels   = 2;
            byte bits       = 16;

            var req = new byte[16];
            req[0] = MsgConnReq;
            req[1] = 1;     // format count
            req[2] = 0xFF;  // ms_index (auto)
            req[3] = 0;     // reserved
            req[4] = (byte)(_rfbSessionId >> 24);
            req[5] = (byte)(_rfbSessionId >> 16);
            req[6] = (byte)(_rfbSessionId >> 8);
            req[7] = (byte)(_rfbSessionId);
            req[8]  = DeviceSpeaker;
            req[9]  = EncSigned;  // 16-bit signed
            req[10] = channels;
            req[11] = bits;
            req[12] = (byte)(sampleRate >> 24);
            req[13] = (byte)(sampleRate >> 16);
            req[14] = (byte)(sampleRate >> 8);
            req[15] = (byte)(sampleRate);
            await _ssl.WriteAsync(req, ct);

            Log("RAP connection request sent");
            return true;
        }
        catch (Exception ex)
        {
            Log($"RAP connect failed: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    // ── Auth helpers ─────────────────────────────────────────────────────────

    /// Type 5 — session-ID challenge-response auth (preferred).
    /// Proves we own the current e-RIC session without sending the password.
    private async Task<bool> AuthWithSessionIdAsync(CancellationToken ct)
    {
        Log("RAP auth: using session-ID (type 5)");
        await _ssl!.WriteAsync(new byte[] { MsgAuthSess }, ct);

        // Server sends 73 bytes: "RAP CHAL=" (9) + 64-byte challenge
        var challengeMsg = await ReadExactAsync(73, ct);
        var prefix = Encoding.ASCII.GetString(challengeMsg, 0, 9);
        if (prefix != "RAP CHAL=")
        {
            Log($"RAP: bad challenge prefix '{prefix}' — falling back to password auth");
            return await AuthWithPasswordAsync(ct);
        }

        var challengeBytes = challengeMsg[9..]; // 64 bytes
        var challengeStr   = Encoding.Latin1.GetString(challengeBytes);
        bool useSha256     = challengeStr.StartsWith("{SHA256}");
        Log($"RAP challenge received, hash={( useSha256 ? "SHA-256" : "MD5" )}");

        // hash = digest(challengeBytes ++ ericSessionId_utf8)
        var sessionIdBytes = Encoding.UTF8.GetBytes(_ericSessionId!);
        byte[] hash;
        if (useSha256)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            sha.TransformBlock(challengeBytes, 0, challengeBytes.Length, null, 0);
            sha.TransformFinalBlock(sessionIdBytes, 0, sessionIdBytes.Length);
            hash = sha.Hash!;
        }
        else
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            md5.TransformBlock(challengeBytes, 0, challengeBytes.Length, null, 0);
            md5.TransformFinalBlock(sessionIdBytes, 0, sessionIdBytes.Length);
            hash = md5.Hash!;
        }

        var hexHash  = BitConverter.ToString(hash).Replace("-", "").ToUpper();
        var response = Encoding.ASCII.GetBytes("RAP RESP=" + hexHash);
        await _ssl.WriteAsync(response, ct);
        Log($"RAP sent challenge response ({response.Length} bytes)");

        return await ReadAuthResponseAsync(ct);
    }

    /// Type 4 — username/password auth (fallback).
    private async Task<bool> AuthWithPasswordAsync(CancellationToken ct)
    {
        Log("RAP auth: using password (type 4)");
        var userBytes = Encoding.UTF8.GetBytes(_username);
        var passBytes = Encoding.UTF8.GetBytes(_password);
        var auth = new byte[6 + userBytes.Length + passBytes.Length];
        auth[0] = MsgAuthPwd;
        auth[1] = 0x00;
        auth[2] = (byte)(userBytes.Length >> 8); auth[3] = (byte)userBytes.Length;
        auth[4] = (byte)(passBytes.Length >> 8); auth[5] = (byte)passBytes.Length;
        userBytes.CopyTo(auth, 6);
        passBytes.CopyTo(auth, 6 + userBytes.Length);
        await _ssl!.WriteAsync(auth, ct);
        return await ReadAuthResponseAsync(ct);
    }

    private async Task<bool> ReadAuthResponseAsync(CancellationToken ct)
    {
        var resp = await ReadExactAsync(8, ct);
        Log($"RAP auth response: {BitConverter.ToString(resp)}");
        if (resp[0] != MsgResponse || resp[1] != 1) { Log("RAP auth failed"); return false; }
        uint reason = (uint)((resp[4] << 24) | (resp[5] << 16) | (resp[6] << 8) | resp[7]);
        if (reason != AuthSuccessReason) { Log($"RAP auth rejected: 0x{reason:X8}"); return false; }
        Log("RAP auth OK");
        return true;
    }

    // ── Receive loop ─────────────────────────────────────────────────────────

    public async Task ReceiveLoopAsync(CancellationToken ct = default)
    {
        if (_ssl is null) return;
        Log("RAP receive loop started");
        int packetCount = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var typeBuf = await ReadExactAsync(1, ct);
                switch (typeBuf[0])
                {
                    case MsgPong:
                        // 1 byte total (already consumed)
                        break;

                    case MsgNotif:
                        // 8 bytes total: [type(1)][flags(3)][errorCode(4)]
                        await ReadExactAsync(7, ct);
                        break;

                    case MsgAudioData:
                        await HandleAudioDataAsync(ct);
                        if (++packetCount <= 5) Log($"RAP audio packet #{packetCount}");
                        break;

                    case MsgResponse:
                        // 8 bytes total: [type(1)][ack(1)][pad(2)][reason(4)]
                        var r = await ReadExactAsync(7, ct);
                        Log($"RAP response: ack={r[0]} reason=0x{(uint)((r[3]<<24)|(r[4]<<16)|(r[5]<<8)|r[6]):X8}");
                        break;

                    case MsgBlockSize:
                        // 8 bytes total
                        await ReadExactAsync(7, ct);
                        break;

                    default:
                        Log($"RAP unknown msg type 0x{typeBuf[0]:X2} — skipping");
                        // Cannot resync — stop
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"RAP receive error: {ex.Message.Split('\n')[0]}"); }
        Log("RAP receive loop ended");
    }

    private async Task HandleAudioDataAsync(CancellationToken ct)
    {
        // Header after type byte (15 bytes):
        // [reserved(1)][pad_hi(1)][pad_lo(1)]
        // [deviceType(1)][encoding(1)][channels(1)][bits(1)]
        // [sampleRate(4 BE)][dataSize(4 BE)]
        var hdr = await ReadExactAsync(15, ct);

        byte deviceType    = hdr[3];
        byte encoding      = hdr[4];
        byte channels      = hdr[5];
        byte bits          = hdr[6];
        int  sampleRate    = (hdr[7] << 24) | (hdr[8] << 16) | (hdr[9] << 8) | hdr[10];
        int  dataSize      = (hdr[11] << 24) | (hdr[12] << 16) | (hdr[13] << 8) | hdr[14];

        if (dataSize <= 0 || dataSize > 1_048_576) // sanity: max 1 MB per packet
        {
            Log($"RAP bad dataSize {dataSize}");
            return;
        }

        var pcm = await ReadExactAsync(dataSize, ct);

        // Only dispatch speaker packets
        if (deviceType == DeviceSpeaker)
        {
            bool isSigned = encoding != EncUnsigned;
            AudioPacketReceived?.Invoke(pcm, sampleRate, channels, bits, isSigned);
        }
    }

    // ── Ping (keepalive) ─────────────────────────────────────────────────────

    public async Task SendPingAsync(CancellationToken ct = default)
    {
        if (_ssl is null) return;
        try { await _ssl.WriteAsync(new byte[] { MsgPing }, ct); }
        catch { }
    }

    // ── I/O helpers ───────────────────────────────────────────────────────────

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct = default)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _ssl!.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException("RAP: connection closed");
            read += n;
        }
        return buf;
    }

    private void Log(string msg) =>
        System.IO.File.AppendAllText(_log, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ssl?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _ssl = null; _tcp = null;
    }
}
