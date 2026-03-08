using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using WinKVM.Framebuffer;
using WinKVM.Input;

namespace WinKVM.Protocol;

/// Main session controller: manages the connection lifecycle, authentication,
/// handshake, and ongoing message dispatch.
///
/// Direct port of ERICSession.swift. All protocol logic is identical;
/// only Apple APIs have been replaced with Windows equivalents.
public sealed class ERICSession : INotifyPropertyChanged
{
    // ── Observable state ─────────────────────────────────────────────────────
    private SessionState _state = SessionState.Disconnected;
    public  SessionState  State
    {
        get => _state;
        private set { _state = value; OnPropertyChanged(); StateChanged?.Invoke(value); }
    }

    private string _statusMessage = "";
    public  string  StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }

    private KvmFramebuffer? _framebuffer;
    public  KvmFramebuffer?  Framebuffer { get => _framebuffer; private set { _framebuffer = value; OnPropertyChanged(); } }

    public bool   IsAudioActive    { get; private set; }
    public bool   IsMounting       { get; private set; }
    public string? MountedImageName { get; private set; }
    public bool   IsSendingText    { get; private set; }
    public int    TextSendProgress { get; private set; }
    public int    TextSendTotal    { get; private set; }
    public int    VmDriveCount     { get; private set; }
    public bool   VmReadOnlyForced { get; private set; }

    public string? CertChallengeFingerprint { get; private set; }
    public string? CertChallengeMessage     { get; private set; }

    // Diagnostics
    public long   TotalBytesRead    { get; private set; }
    public long   TotalBytesWritten { get; private set; }
    public double CurrentFps        { get; private set; }
    public double AvgFps            { get; private set; }

    public ObservableCollection<KvmPort>   Ports       { get; } = [];
    public ObservableCollection<UsbProfile> UsbProfiles { get; } = [];
    public string? ActivePortId     { get; private set; }
    public ushort? ActiveUsbProfileId { get; private set; }
    public byte[]  MouseCaps        { get; private set; } = [];
    public VideoSettings? VideoSettings { get; private set; }
    public (byte, byte)?  VideoQuality   { get; private set; }
    public string?        KeyboardLayout { get; private set; }
    public Dictionary<string, string> ConnectionParams { get; } = new();
    public string? AudioSupport     { get; private set; }

    // Events
    public event Action<SessionState>? StateChanged;
    public event Func<string, string, Task<bool>>? CertificateChallenge;
    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Internal ─────────────────────────────────────────────────────────────
    private ERICConnection? _conn;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Decoders
    private readonly HextileDecoder _hextileDecoder = new();
    private readonly ICTDecoder     _ictDecoder     = new();
    private int _ictLogCount;

    // Dirty rects
    private readonly List<(int x,int y,int w,int h)> _dirtyRects = [];

    // Credentials (kept only during session)
    private string? _host, _username, _password;
    private ushort  _port = 443;
    private string? _targetPortId; // first port ID from PortList, sent in KvmSwitchEvent

    // Reconnect
    private string? _lastHost, _lastUsername, _lastPassword;
    private ushort  _lastPort = 443;

    public string? CurrentHost => _host ?? _lastHost;
    public int FramebufferWidth  => _framebuffer?.Width  ?? 0;
    public int FramebufferHeight => _framebuffer?.Height ?? 0;

    // FPS
    private int   _fpsCount;
    private DateTime _fpsLast = DateTime.Now;

    // Renderer reference (set by the view)
    public Rendering.D3DFramebufferControl? Renderer { get; set; }

    // ── Connection ────────────────────────────────────────────────────────────

    public void Connect(string host, ushort port, string username, string password)
    {
        if (State == SessionState.Connecting) return;

        _lastHost = _host = host;
        _lastPort = _port = port;
        _lastUsername = _username = username;
        _lastPassword = _password = password;

        State = SessionState.Connecting;
        StatusMessage = "Connecting...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => PerformConnectionAsync(_cts.Token));
    }

    public void Reconnect()
    {
        if (_lastHost is null || _lastUsername is null || _lastPassword is null) return;
        Connect(_lastHost, _lastPort, _lastUsername, _lastPassword);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        ReleaseResources();
        State = SessionState.Disconnected;
        StatusMessage = "Disconnected";
    }

    private void ReleaseResources()
    {
        _conn?.Disconnect();
        _conn = null;
        _cts?.Cancel();
        _cts  = null;
        _framebuffer?.Dispose();
        Framebuffer = null;
        Ports.Clear(); UsbProfiles.Clear(); MouseCaps = [];
        AudioSupport = null; VideoSettings = null; VideoQuality = null;
        KeyboardLayout = null; ConnectionParams.Clear();
        _host = _username = _password = null;
        _hextileDecoder.Reset();
        _dirtyRects.Clear();
    }

    // ── Protocol handshake ────────────────────────────────────────────────────

    private static readonly string _logPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "winkvm_proto.log");

    private async Task PerformConnectionAsync(CancellationToken ct)
    {
        // Retry loop: auto-reconnect on server-initiated disconnect.
        // Gives up after an auth failure or user cancel.
        while (!ct.IsCancellationRequested)
        {
            var stage = "connecting";
            bool authFailed = false;
            try
            {
                _conn = new ERICConnection();
                await _conn.ConnectAsync(_host!, _port,
                    CertificateChallenge is null ? null : (fp, msg) => CertificateChallenge.Invoke(fp, msg), ct);

                stage = "hello";
                System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] TLS connected, sending hello\n");
                await _conn.WriteAsync(System.Text.Encoding.ASCII.GetBytes("e-RIC AUTH="), ct);

                stage = "version handshake";
                var ver = await _conn.ReadAsync(16, ct);
                var verStr = System.Text.Encoding.ASCII.GetString(ver);
                System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Ver: '{verStr.Replace("\n","\\n")}'\n");
                await _conn.WriteAsync(ver, ct);

                State = SessionState.Authenticating;
                StatusMessage = $"Version: {verStr.TrimEnd()}";
                _selectedAuthMethod = 0;
                _authSuccessful = false;

                await MessageLoopAsync(ct);
                return; // clean exit (e.g. Disconnect() called)
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex.Message.Contains("Authentication failed"))
            {
                System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] FAILED at [{stage}]: {ex}\n");
                State = SessionState.Disconnected;
                StatusMessage = $"Connection error: {FriendlyError(ex)}";
                authFailed = true;
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] FAILED at [{stage}]: {ex}\n");
                // Server-initiated disconnect — reconnect after a short delay
                State = SessionState.Connecting;
                StatusMessage = "Reconnecting...";
            }
            finally
            {
                ReleaseResources();
            }

            if (authFailed) return;

            // Wait before reconnecting
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    // Auth state for the unified message loop
    private byte _selectedAuthMethod;
    private bool _authSuccessful;

    // ── Message loop (unified auth + init + runtime, mirrors Swift) ──────────

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        bool inHandshake = true; // true until first ServerFBFormat received
        while (!ct.IsCancellationRequested)
        {
            byte msgType = await _conn!.ReadByteAsync(ct);
            System.IO.File.AppendAllText(_logPath,
                $"[{DateTime.Now:HH:mm:ss}] Msg 0x{msgType:X2} ({(Enum.IsDefined(typeof(ServerMessage),(ServerMessage)msgType) ? ((ServerMessage)msgType).ToString() : "?")}) inHandshake={inHandshake}\n");

            switch (msgType)
            {
                // ── Auth flow ─────────────────────────────────────────────────
                case (byte)ServerMessage.AuthCaps: // 0x20: 1 byte bitmask
                {
                    var caps = (await _conn.ReadAsync(1, ct))[0];
                    System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] AuthCaps 0x{caps:X2} (plain={(caps & AuthMethod.Plain)!=0} md5={(caps & AuthMethod.Md5)!=0})\n");
                    _selectedAuthMethod = (caps & AuthMethod.Plain) != 0 ? AuthMethod.Plain
                                        : (caps & AuthMethod.Md5)   != 0 ? AuthMethod.Md5
                                        : throw new IOException($"No supported auth method (caps=0x{caps:X2})");
                    var userBytes = Encoding.UTF8.GetBytes(_username!);
                    var login = new BinaryWriter2();
                    login.WriteU8((byte)ClientMessage.Login);
                    login.WriteU8(_selectedAuthMethod);
                    login.WriteU8((byte)(userBytes.Length + 1)); // length incl. null
                    login.WriteU8(0);   // padding
                    login.WriteU32(0);  // flags
                    login.WriteBytes(userBytes);
                    login.WriteU8(0);   // null terminator
                    var loginArr = login.ToArray();
                    System.IO.File.AppendAllText(_logPath,
                        $"[{DateTime.Now:HH:mm:ss}] Login hex={BitConverter.ToString(loginArr)} userLen={userBytes.Length}\n");
                    await _conn.WriteAsync(loginArr, ct);
                    break;
                }

                case (byte)ServerMessage.SessionChallenge: // 0x21: 1 byte len + challenge
                {
                    var challengeLen = (await _conn.ReadAsync(1, ct))[0];
                    var challenge    = challengeLen > 0 ? await _conn.ReadAsync(challengeLen, ct) : [];
                    System.IO.File.AppendAllText(_logPath,
                        $"[{DateTime.Now:HH:mm:ss}] SessionChallenge len={challengeLen} bytes={BitConverter.ToString(challenge)}\n");
                    string response  = _selectedAuthMethod == AuthMethod.Md5
                        ? ERICAuth.Md5ChallengeResponse(challenge, _password!)
                        : _password!;
                    var respBytes = Encoding.UTF8.GetBytes(response);
                    var resp = new BinaryWriter2();
                    resp.WriteU8((byte)ClientMessage.ChallengeResponse);
                    resp.WriteU8((byte)respBytes.Length);
                    resp.WriteBytes(respBytes);
                    System.IO.File.AppendAllText(_logPath,
                        $"[{DateTime.Now:HH:mm:ss}] ChallengeResponse method={_selectedAuthMethod} respLen={respBytes.Length}\n");
                    await _conn.WriteAsync(resp.ToArray(), ct);
                    break;
                }

                case (byte)ServerMessage.AuthSuccessful: // 0x22: + 7 bytes (pad + connectionFlags)
                    await _conn.ReadAsync(7, ct);
                    _authSuccessful = true;
                    System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] AuthSuccessful\n");
                    // Stay in Authenticating state until ServerFBFormat (matching SwiftKVM)
                    StatusMessage = "Authenticated — waiting for framebuffer...";
                    break;

                // ── Init flow ─────────────────────────────────────────────────
                case (byte)ServerMessage.ServerInit: // 0x05: 7 bytes (3 pad + 4 serverId)
                {
                    var siData = await _conn.ReadAsync(7, ct);
                    var sir = new BinaryReader2(siData);
                    sir.Skip(3);
                    System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] ServerInit serverId={sir.ReadU32()}\n");
                    break;
                }

                case (byte)ServerMessage.Utf8String: // 0x07: 1 pad + 2 len + string → ClientInit
                    await HandleUtf8StringAsync(inHandshake, ct);
                    break;

                // ── Runtime ───────────────────────────────────────────────────
                case (byte)ServerMessage.FramebufferUpdate:
                    await HandleFramebufferUpdateAsync(ct);
                    break;

                case (byte)ServerMessage.ServerFBFormat: // 0x80: pixel format descriptor
                    await HandleServerFBFormatAsync(ct);
                    if (inHandshake)
                    {
                        inHandshake = false;
                        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Handshake done — sending init\n");
                        await SendSetEncodingsAsync(ct);
                        await SendSetPixelFormatAsync(ct);
                        ushort fbW = (ushort)(_framebuffer?.Width ?? 1920);
                        ushort fbH = (ushort)(_framebuffer?.Height ?? 1080);
                        await SendFramebufferUpdateRequestAsync(0, 0, fbW, fbH, incremental: false, ct);
                        State = SessionState.Connected;
                        StatusMessage = $"Connected ({fbW}x{fbH})";
                        _ = PingLoopAsync(ct);           // keepalive: ping every 10 s
                        _ = PointerKeepaliveAsync(ct);   // keepalive: null pointer every 1 s (prevents KVM inactivity timeout)
                    }
                    else
                    {
                        // Subsequent ServerFBFormat after resize: request new full frame
                        await SendFramebufferUpdateRequestAsync(0, 0,
                            (ushort)(_framebuffer?.Width ?? 1920), (ushort)(_framebuffer?.Height ?? 1080),
                            incremental: false, ct);
                    }
                    break;

                case (byte)ServerMessage.PingRequest:
                    await HandlePingAsync(ct);
                    break;

                case (byte)ServerMessage.PingReply: // 0x95: server echoes our proactive ping
                    await _conn.ReadAsync(7, ct);   // discard: pad(3) + serial(4)
                    break;

                case (byte)ServerMessage.ConnectionParameterList:
                    await HandleConnectionParamsAsync(ct);
                    break;

                case (byte)ServerMessage.ServerCommand:
                    await HandleServerCommandAsync(ct);
                    break;

                case (byte)ServerMessage.PortList:
                case (byte)ServerMessage.PortListExt:
                    await HandlePortListAsync(msgType == (byte)ServerMessage.PortListExt, ct);
                    break;

                case (byte)ServerMessage.UsbProfileList:
                    await HandleUsbProfileListAsync(ct);
                    break;

                case (byte)ServerMessage.VideoSettingsS2C:
                    await HandleVideoSettingsAsync(ct);
                    break;

                case (byte)ServerMessage.VideoQualityS2C:
                    await HandleVideoQualityAsync(ct);
                    break;

                case (byte)ServerMessage.KeyboardLayout:
                    await HandleKeyboardLayoutAsync(ct);
                    break;

                case (byte)ServerMessage.MouseCapsResponse:
                    await HandleMouseCapsAsync(ct);
                    break;

                case (byte)ServerMessage.VirtualMediaConfig:
                    await HandleVirtualMediaConfigAsync(ct);
                    break;

                case (byte)ServerMessage.VmMountsResponse: // 0xA6: option(1)+index(1)+pad(1)+retCode(4)
                    await _conn.ReadAsync(7, ct);
                    break;

                case (byte)ServerMessage.VmShareTable: // 0xA7: entryCount(1)+per entry: hostLen(1)+imageLen(1)+host+image
                {
                    int vmCount = (await _conn.ReadAsync(1, ct))[0];
                    for (int vi = 0; vi < vmCount; vi++)
                    {
                        var lens = await _conn.ReadAsync(2, ct);
                        if (lens[0] > 0) await _conn.ReadAsync(lens[0], ct);
                        if (lens[1] > 0) await _conn.ReadAsync(lens[1], ct);
                    }
                    break;
                }

                case (byte)ServerMessage.UserNotification: // 0x03: flags(1) + pad(2) + errorCode(4)
                {
                    var unData = await _conn.ReadAsync(7, ct);
                    var unr = new BinaryReader2(unData);
                    var unFlags = unr.ReadU8(); unr.Skip(2);
                    var unCode  = unr.ReadU32();
                    System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] UserNotification flags=0x{unFlags:X2} code={unCode}\n");
                    break;
                }

                case (byte)ServerMessage.OsdState: // 0x10: blanking(1) + timeout(2) + msgLen(2) + msg
                {
                    var osdHdr = await _conn.ReadAsync(5, ct);
                    var osdr = new BinaryReader2(osdHdr);
                    osdr.Skip(3); // blanking + timeout
                    int osdMsgLen = osdr.ReadU16();
                    if (osdMsgLen > 0 && osdMsgLen <= 4096)
                        await _conn.ReadAsync(osdMsgLen, ct);
                    break;
                }

                case (byte)ServerMessage.AckPixelFormat: // 0x13: 19 bytes (pad3+pixelFormat16)
                    await _conn.ReadAsync(19, ct);
                    break;

                case (byte)ServerMessage.ServerRCMessage: // 0x83: pad(3)+size(4)+data
                {
                    var rcHdr = await _conn.ReadAsync(7, ct);
                    var rcr = new BinaryReader2(rcHdr); rcr.Skip(3);
                    int rcSize = (int)rcr.ReadU32();
                    if (rcSize > 0 && rcSize <= 1 << 20) await _conn.ReadAsync(rcSize, ct);
                    break;
                }

                case (byte)ServerMessage.BandwidthRequest: // 0x96: stage(1)+len(2)+data
                {
                    var bwHdr = await _conn.ReadAsync(3, ct);
                    int bwLen = (bwHdr[1] << 8) | bwHdr[2];
                    if (bwLen > 0 && bwLen <= 65536) await _conn.ReadAsync(bwLen, ct);
                    break;
                }

                case (byte)ServerMessage.TerminalConfig: // 0xAD: 3 bytes
                    await _conn.ReadAsync(3, ct);
                    break;

                case (byte)ServerMessage.TerminalInputSettings: // 0xAE: 4 bytes
                    await _conn.ReadAsync(4, ct);
                    break;

                default:
                    System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Unknown msg 0x{msgType:X2} — disconnecting\n");
                    throw new IOException($"Unknown server message 0x{msgType:X2}");

            }
        }
    }

    private async Task HandleUtf8StringAsync(bool inHandshake, CancellationToken ct)
    {
        await _conn!.ReadAsync(1, ct); // 1 padding byte
        var lenBytes = await _conn.ReadAsync(2, ct);
        int len = (lenBytes[0] << 8) | lenBytes[1];
        if (len > 0) await _conn.ReadAsync(len, ct);

        if (inHandshake)
        {
            // ClientInit: [type, 0, initflags(2)] — bit 3 = extended port ID
            var initMsg = new BinaryWriter2();
            initMsg.WriteU8((byte)ClientMessage.ClientInit);
            initMsg.WriteU8(0);
            initMsg.WriteU16(0x0008);
            await _conn.WriteAsync(initMsg.ToArray(), ct);

            // KvmSwitchEvent: [type, pad, portIdLen(2 BE), portId bytes]
            var kvmMsg = new BinaryWriter2();
            kvmMsg.WriteU8((byte)ClientMessage.KvmSwitchEvent);
            kvmMsg.WriteU8(0);
            var portIdBytes = _targetPortId is not null ? Encoding.UTF8.GetBytes(_targetPortId) : [];
            kvmMsg.WriteU16((ushort)portIdBytes.Length);
            if (portIdBytes.Length > 0) kvmMsg.WriteBytes(portIdBytes);
            await _conn.WriteAsync(kvmMsg.ToArray(), ct);
            System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Sent ClientInit + KvmSwitchEvent portId='{_targetPortId ?? ""}'\n");
        }
    }

    // ── Framebuffer update ────────────────────────────────────────────────────

    private async Task HandleFramebufferUpdateAsync(CancellationToken ct)
    {
        var hdrBytes = await _conn!.ReadAsync(7, ct);
        var r = new BinaryReader2(hdrBytes);
        byte   flags    = r.ReadU8();
        ushort numRects = r.ReadU16();
        uint   size     = r.ReadU32();

        _dirtyRects.Clear();

        bool useChunkReader = (flags & 0x08) != 0;
        if ((flags & 0x01) != 0) await _conn.ReadAsync(8, ct); // timestamp

        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] FBUpdate flags=0x{flags:X2} numRects={numRects} size={size}\n");

        // Pipeline: request next frame BEFORE decoding so the server prepares it
        // while we are decoding. Matches SwiftKVM's pipelineRequest pattern.
        await SendFramebufferUpdateRequestAsync(0, 0, (ushort)(_framebuffer?.Width ?? 1920),
            (ushort)(_framebuffer?.Height ?? 1080), incremental: true, ct);

        if (useChunkReader)
            await HandleChunkWiseFBUpdateAsync(numRects, (int)size, ct);
        else
            await HandleNormalFBUpdateAsync(numRects, ct);

        // FPS tracking
        _fpsCount++;
        var now = DateTime.Now;
        var elapsed = (now - _fpsLast).TotalSeconds;
        if (elapsed >= 1.0)
        {
            CurrentFps = _fpsCount / elapsed;
            AvgFps     = (AvgFps * 0.9) + (CurrentFps * 0.1);
            _fpsCount  = 0;
            _fpsLast   = now;
        }
    }

    // ── Chunk-wise FBUpdate (flags & 0x08) ───────────────────────────────────
    // Rect headers are 12 bytes (no dataSize field); all chunk data is read
    // at once into a flat buffer, then parsed with a cursor (SwiftKVM pattern).
    private async Task HandleChunkWiseFBUpdateAsync(int numRects, int sizeHint, CancellationToken ct)
    {
        var reader  = new ChunkReader(_conn!);
        var allData = await reader.ReadAllAsync(sizeHint, ct);
        int cursor  = 0;

        for (int ri = 0; ri < numRects && cursor + 12 <= allData.Length; ri++)
        {
            var rh  = new BinaryReader2(allData.AsSpan(cursor, 12));
            ushort rx = rh.ReadU16(), ry = rh.ReadU16(), rw = rh.ReadU16(), rh2 = rh.ReadU16();
            int enc = rh.ReadI32();
            cursor += 12;

            System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}]   rect[{ri}] ({rx},{ry}) {rw}x{rh2} enc=0x{enc:X} [chunk]\n");

            if ((RfbEncoding)enc == RfbEncoding.NewFBSize)
            {
                if (rw > 0 && rh2 > 0)
                {
                    _framebuffer?.Dispose();
                    Framebuffer = new KvmFramebuffer(rw, rh2);
                    Renderer?.EnsureTexture(rw, rh2);
                }
                continue;
            }
            if ((RfbEncoding)enc == RfbEncoding.LastRect) break;
            if (rw == 0 || rh2 == 0 || cursor >= allData.Length) continue;

            int encType = enc & 0xFF;
            if (encType is 129 or 131 or 132 or 133)
            {
                // ICT: remaining buffer is the compressed frame
                int subenc = (enc >> 12) & 0xF;
                var slice  = allData[cursor..];
                var planes = _ictDecoder.Decode(slice, rw, rh2, subenc);
                if (planes is not null && Renderer is { } ren)
                    ren.UploadYCbCr(planes);
                cursor = allData.Length; // ICT consumes the rest
            }
            else if ((RfbEncoding)enc == RfbEncoding.Raw && _framebuffer is not null)
            {
                int sz = rw * rh2 * 4;
                if (cursor + sz <= allData.Length)
                    RawDecoder.Decode(allData[cursor..(cursor + sz)], _framebuffer, rx, ry, rw, rh2);
                cursor += sz;
                _dirtyRects.Add((rx, ry, rw, rh2));
                Renderer?.UploadFramebuffer(_framebuffer, _dirtyRects.Count > 0 ? _dirtyRects : null);
            }
            else if ((RfbEncoding)enc == RfbEncoding.Hextile && _framebuffer is not null)
            {
                var slice = allData[cursor..];
                var (hFills, hRawTiles, _) = _hextileDecoder.Decode(slice, _framebuffer, rx, ry, rw, rh2);
                Renderer?.ExecuteFills([.. hFills], _framebuffer.Width, _framebuffer.Height);
                cursor = allData.Length;
            }
            else
            {
                System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}]   unknown enc 0x{enc:X} [chunk]\n");
                cursor = allData.Length;
            }
        }
    }

    // ── Normal FBUpdate (flags & 0x08 == 0) ──────────────────────────────────
    // Rect headers are 16 bytes (includes 4-byte dataSize).
    private async Task HandleNormalFBUpdateAsync(int numRects, CancellationToken ct)
    {
        var fills    = new List<FillCommand>();
        var rawTiles = new List<RawTileCommand>();
        var rawDataMs = new MemoryStream();

        for (int ri = 0; ri < numRects; ri++)
        {
            var rectHdrBytes = await _conn!.ReadAsync(16, ct);
            var rh   = new BinaryReader2(rectHdrBytes);
            ushort rx = rh.ReadU16(), ry = rh.ReadU16(), rw = rh.ReadU16(), rh2 = rh.ReadU16();
            int enc  = rh.ReadI32();
            uint ds  = rh.ReadU32();

            System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}]   rect[{ri}] ({rx},{ry}) {rw}x{rh2} enc=0x{enc:X} ds={ds}\n");

            if ((RfbEncoding)enc == RfbEncoding.NewFBSize)
            {
                if (rw > 0 && rh2 > 0)
                {
                    _framebuffer?.Dispose();
                    Framebuffer = new KvmFramebuffer(rw, rh2);
                    Renderer?.EnsureTexture(rw, rh2);
                    System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}]   NewFBSize → {rw}x{rh2}\n");
                }
                continue;
            }
            if ((RfbEncoding)enc == RfbEncoding.LastRect) break;
            if (rw == 0 || rh2 == 0 || ds == 0) continue;

            var rectData = await _conn.ReadAsync((int)ds, ct);
            if (_framebuffer is null) continue;

            int encType = enc & 0xFF;
            switch ((RfbEncoding)enc)
            {
                case RfbEncoding.Raw:
                    RawDecoder.Decode(rectData, _framebuffer, rx, ry, rw, rh2);
                    _dirtyRects.Add((rx, ry, rw, rh2));
                    break;

                case RfbEncoding.Hextile:
                    var (hFills, hRawTiles, hRawData) = _hextileDecoder.Decode(rectData, _framebuffer, rx, ry, rw, rh2);
                    fills.AddRange(hFills);
                    int offset = (int)rawDataMs.Length;
                    rawDataMs.Write(hRawData);
                    rawTiles.AddRange(hRawTiles.Select(t => t with { DataOffset = t.DataOffset + offset }));
                    break;

                default:
                    if (encType is 129 or 131 or 132 or 133)
                    {
                        int subenc = (enc >> 12) & 0xF;
                        var planes = _ictDecoder.Decode(rectData, rw, rh2, subenc);
                        if (planes is not null && Renderer is { } ren)
                            ren.UploadYCbCr(planes);
                    }
                    else
                        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}]   unknown enc 0x{enc:X} ds={ds}\n");
                    break;
            }
        }

        if (Renderer is { } r2 && _framebuffer is { } fb)
        {
            if (fills.Count > 0)
                r2.ExecuteFills([.. fills], fb.Width, fb.Height);
            else if (_dirtyRects.Count > 0)
                r2.UploadFramebuffer(fb, _dirtyRects);
        }
    }

    private async Task HandleServerFBFormatAsync(CancellationToken ct)
    {
        // isUnsupported(1) + width(2) + height(2) + pixelFormat(16) = 21 bytes
        var hdr = await _conn!.ReadAsync(21, ct);
        var r   = new BinaryReader2(hdr);
        r.Skip(1); // isUnsupported
        ushort w = r.ReadU16(), h = r.ReadU16();
        // skip 16 bytes of pixel format (bpp+depth+bigEndian+trueColour+redMax+greenMax+blueMax+shifts+pad)

        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] ServerFBFormat {w}x{h}\n");

        if (w > 0 && h > 0 && (_framebuffer is null || _framebuffer.Width != w || _framebuffer.Height != h))
        {
            _framebuffer?.Dispose();
            Framebuffer = new KvmFramebuffer(w, h);
            Renderer?.EnsureTexture(w, h);
        }
    }

    // ── Ping ──────────────────────────────────────────────────────────────────

    private uint _pingSerial;

    /// Respond to a server PingRequest (0x94).
    /// Wire format (after msgType): pad(1) + pad(2) + serial(4) = 7 bytes total.
    private async Task HandlePingAsync(CancellationToken ct)
    {
        var data = await _conn!.ReadAsync(7, ct); // 3 pad + 4 serial
        var reply = new BinaryWriter2();
        reply.WriteU8((byte)ClientMessage.PingReply);
        reply.WriteBytes(data); // echo all 7 bytes back
        await _conn.WriteAsync(reply.ToArray(), ct);
    }

    /// Pointer keepalive: send a null pointer event every 1 s to prevent the KVM's
    /// server-side inactivity timeout from closing the session when the user isn't
    /// actively moving the mouse over the KVM view.
    private async Task PointerKeepaliveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (_conn is not null)
                    await SendPointerEventAsync(0, 0, 0, ct: ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* connection gone */ }
    }

    /// Proactive keepalive: send PingRequest every 10 s (matching SwiftKVM ERICPing).
    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                _pingSerial++;
                var msg = new BinaryWriter2();
                msg.WriteU8((byte)ClientMessage.PingRequest);
                msg.WriteU8(0); // pad
                msg.WriteU32(_pingSerial);
                msg.WriteU16(0); // pad
                await _conn!.WriteAsync(msg.ToArray(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* connection gone */ }
    }

    // ── ConnectionParameterList ───────────────────────────────────────────────

    private async Task HandleConnectionParamsAsync(CancellationToken ct)
    {
        var count = (await _conn!.ReadAsync(1, ct))[0];
        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] ConnectionParameterList count={count}\n");
        for (int i = 0; i < count; i++)
        {
            var lens     = await _conn.ReadAsync(2, ct);
            int nameLen  = lens[0];
            int valueLen = lens[1];
            var nameBytes = nameLen  > 0 ? await _conn.ReadAsync(nameLen,  ct) : [];
            var valBytes  = valueLen > 0 ? await _conn.ReadAsync(valueLen, ct) : [];
            var name  = Encoding.UTF8.GetString(nameBytes);
            var value = Encoding.UTF8.GetString(valBytes);
            System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}]   param[{i}] {name}={value}\n");
            ConnectionParams[name] = value;
        }
        if (ConnectionParams.TryGetValue("audio_support", out var au))
            AudioSupport = au;
    }

    // ── ServerCommand ─────────────────────────────────────────────────────────

    private async Task HandleServerCommandAsync(CancellationToken ct)
    {
        // pad(1) + commandLen(2 BE) + paramsLen(2 BE) + command + params
        var hdr = await _conn!.ReadAsync(5, ct);
        int cmdLen    = (hdr[1] << 8) | hdr[2];
        int paramsLen = (hdr[3] << 8) | hdr[4];
        if (cmdLen > 4096 || paramsLen > 65536)
            throw new IOException($"ServerCommand too large: cmd={cmdLen} params={paramsLen}");
        var cmd    = cmdLen    > 0 ? Encoding.UTF8.GetString(await _conn.ReadAsync(cmdLen,    ct)) : "";
        var param  = paramsLen > 0 ? Encoding.UTF8.GetString(await _conn.ReadAsync(paramsLen, ct)) : "";
        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] ServerCommand '{cmd}' params='{param}'\n");
    }

    // ── Port list ─────────────────────────────────────────────────────────────

    private async Task HandlePortListAsync(bool ext, CancellationToken ct)
    {
        // pad(1) + numPorts(2 BE) — same format for PortList and PortListExt
        var hdr = await _conn!.ReadAsync(3, ct);
        int numPorts = (hdr[1] << 8) | hdr[2];
        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] PortList numPorts={numPorts}\n");
        Ports.Clear();
        _targetPortId = null;
        for (int i = 0; i < numPorts; i++)
        {
            // kvmPerm(1)+vmPerm(1)+powerPerm(1)+pad(1)+portNoLen(2)+portIdLen(2)+portNameLen(2)+cimTypeLen(2)=12
            var ph = await _conn.ReadAsync(12, ct);
            var pr = new BinaryReader2(ph);
            pr.Skip(4); // kvmPerm + vmPerm + powerPerm + pad
            int portNoLen   = pr.ReadU16();
            int portIdLen   = pr.ReadU16();
            int portNameLen = pr.ReadU16();
            int cimTypeLen  = pr.ReadU16();
            var portNo   = portNoLen   > 0 ? Encoding.UTF8.GetString(await _conn.ReadAsync(portNoLen,   ct)) : "";
            var portId   = portIdLen   > 0 ? Encoding.UTF8.GetString(await _conn.ReadAsync(portIdLen,   ct)) : "";
            var portName = portNameLen > 0 ? Encoding.UTF8.GetString(await _conn.ReadAsync(portNameLen, ct)) : "";
            if (cimTypeLen > 0) await _conn.ReadAsync(cimTypeLen, ct);
            System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}]   Port[{i}] no='{portNo}' id='{portId}' name='{portName}'\n");
            if (!string.IsNullOrEmpty(portId))
            {
                Ports.Add(new KvmPort(portId, (ushort)(i + 1), portName));
                _targetPortId ??= portId;
            }
        }
    }

    // ── USB profiles ──────────────────────────────────────────────────────────

    private async Task HandleUsbProfileListAsync(CancellationToken ct)
    {
        // entryCount(2 BE) + per entry: nameLen(1)+descLen(2 BE)+flags(1)+profileId(2 BE)+name+desc
        var cntBytes = await _conn!.ReadAsync(2, ct);
        int cnt = (cntBytes[0] << 8) | cntBytes[1];
        UsbProfiles.Clear();
        ActiveUsbProfileId = null;
        for (int i = 0; i < cnt && cnt <= 256; i++)
        {
            var eh      = await _conn.ReadAsync(6, ct);
            int nameLen = eh[0];
            int descLen = (eh[1] << 8) | eh[2];
            byte flags  = eh[3];
            ushort id   = (ushort)((eh[4] << 8) | eh[5]);
            var name    = nameLen > 0 ? Encoding.UTF8.GetString(await _conn.ReadAsync(nameLen, ct)) : "";
            if (descLen > 0) await _conn.ReadAsync(descLen, ct);
            UsbProfiles.Add(new UsbProfile(id, name));
            if ((flags & 1) != 0) ActiveUsbProfileId = id;
        }
        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] UsbProfileList: {cnt} profiles\n");
    }

    // ── Video / keyboard / mouse ──────────────────────────────────────────────

    private async Task HandleVideoSettingsAsync(CancellationToken ct)
    {
        // pad(1)+brightnessRGB(3)+contrastRGB(3)+autoColorCal(1)+autoAdjust(1)
        // +clock(2)+phase(2)+offsetX(2)+offsetY(2)+resX(2)+resY(2)+refreshRate(2)+noiseFilter(2)+offsetYMax(2)
        // = 27 bytes total
        var data = await _conn!.ReadAsync(27, ct);
        var r = new BinaryReader2(data);
        r.Skip(9); // pad + brightness + contrast + autoCC + autoAdj
        r.Skip(4); // clock + phase
        r.Skip(4); // offsetX + offsetY
        ushort resX = r.ReadU16(), resY = r.ReadU16();
        VideoSettings = new VideoSettings("Unknown", resX, resY);
        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] VideoSettings {resX}x{resY}\n");
    }

    private async Task HandleVideoQualityAsync(CancellationToken ct)
    {
        var d = await _conn!.ReadAsync(2, ct);
        VideoQuality = (d[0], d[1]);
    }

    private async Task HandleKeyboardLayoutAsync(CancellationToken ct)
    {
        // pad(1) + len(2 BE) + string
        await _conn!.ReadAsync(1, ct); // pad
        var lenBytes = await _conn.ReadAsync(2, ct);
        int len = (lenBytes[0] << 8) | lenBytes[1];
        if (len > 0 && len <= 1024)
        {
            var data = await _conn.ReadAsync(len, ct);
            KeyboardLayout = Encoding.UTF8.GetString(data);
        }
    }

    private async Task HandleMouseCapsAsync(CancellationToken ct)
    {
        var cnt = (await _conn!.ReadAsync(1, ct))[0];
        MouseCaps = cnt > 0 ? await _conn.ReadAsync(cnt, ct) : [];
    }

    private async Task HandleVirtualMediaConfigAsync(CancellationToken ct)
    {
        var numDrives = (await _conn!.ReadAsync(1, ct))[0];
        if (numDrives > 0) await _conn.ReadAsync(numDrives, ct); // driveTypes
        VmDriveCount = numDrives;
        System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] VirtualMediaConfig: {numDrives} drives\n");
    }

    // ── Client → Server messages ──────────────────────────────────────────────

    private async Task SendFramebufferUpdateRequestAsync(ushort x, ushort y, ushort w, ushort h, bool incremental, CancellationToken ct)
    {
        var msg = new BinaryWriter2();
        msg.WriteU8((byte)ClientMessage.FramebufferUpdateRequest);
        msg.WriteU8(incremental ? (byte)1 : (byte)0);
        msg.WriteU16(x); msg.WriteU16(y);
        msg.WriteU16(w); msg.WriteU16(h);
        await _conn!.WriteAsync(msg.ToArray(), ct);
    }

    private async Task SendSetPixelFormatAsync(CancellationToken ct)
    {
        var msg = new BinaryWriter2();
        msg.WriteU8((byte)ClientMessage.SetPixelFormat);
        msg.WriteBytes([0, 0, 0]); // padding
        var pf = PixelFormat.Bgrx32;
        msg.WriteU8(pf.BitsPerPixel); msg.WriteU8(pf.Depth);
        msg.WriteU8(pf.BigEndian   ? (byte)1 : (byte)0);
        msg.WriteU8(pf.TrueColour  ? (byte)1 : (byte)0);
        msg.WriteU16(pf.RedMax); msg.WriteU16(pf.GreenMax); msg.WriteU16(pf.BlueMax);
        msg.WriteU8(pf.RedShift); msg.WriteU8(pf.GreenShift); msg.WriteU8(pf.BlueShift);
        msg.WriteBytes([0, 0, 0]); // padding
        await _conn!.WriteAsync(msg.ToArray(), ct);
    }

    private async Task SendSetEncodingsAsync(CancellationToken ct)
    {
        // 255 = auto-hardware (let server pick best HW encoding)
        // 0xB083 = ICT4K YCbCr420 JPEG Q75 (encoding 131 | subencoding 11<<12)
        const int Ict4k420Jpeg75 = 131 | (11 << 12); // 0xB083
        var encodings = new[]
        {
            255,                        // auto-hardware
            Ict4k420Jpeg75,             // ICT 4K YCbCr420 JPEG Q75
            (int)RfbEncoding.NewFBSize,
            (int)RfbEncoding.LastRect,
        };
        var msg = new BinaryWriter2();
        msg.WriteU8((byte)ClientMessage.SetEncodings);
        msg.WriteU8(0); // padding
        msg.WriteU16((ushort)encodings.Length);
        foreach (var e in encodings) msg.WriteI32(e);
        await _conn!.WriteAsync(msg.ToArray(), ct);
    }

    public async Task SendKeyEventAsync(ushort keyCode, bool pressed, CancellationToken ct = default)
    {
        var msg = new BinaryWriter2();
        msg.WriteU8((byte)ClientMessage.KeyEvent);
        msg.WriteU8(pressed ? (byte)1 : (byte)0);
        msg.WriteU16(0); // padding
        msg.WriteU32(keyCode);
        await _conn!.WriteAsync(msg.ToArray(), ct);
    }

    // Wire format: msgType(1) + buttonMask(1) + x(2) + y(2) + z(2) = 8 bytes
    // The z field is 0 for moves/clicks; non-zero for scroll wheel.
    public async Task SendPointerEventAsync(ushort x, ushort y, byte buttonMask, short z = 0, CancellationToken ct = default)
    {
        var msg = new BinaryWriter2();
        msg.WriteU8((byte)ClientMessage.PointerEvent);
        msg.WriteU8(buttonMask);
        msg.WriteU16(x); msg.WriteU16(y);
        msg.WriteU16((ushort)(short)z); // scroll wheel (hi_res_mouse extension)
        await _conn!.WriteAsync(msg.ToArray(), ct);
    }

    public async Task SendScrollEventAsync(ushort x, ushort y, byte buttonMask, short z, CancellationToken ct = default)
    {
        await SendPointerEventAsync(x, y, buttonMask, z, ct);
    }

    public void SendCtrlAltDel()
    {
        _ = Task.Run(async () =>
        {
            var ct = CancellationToken.None;
            await SendKeyEventAsync(KeyboardHandler.RaritanKeyCode(Windows.System.VirtualKey.LeftControl) ?? 54, true, ct);
            await SendKeyEventAsync(KeyboardHandler.RaritanKeyCode(Windows.System.VirtualKey.LeftMenu) ?? 55, true, ct);
            await SendKeyEventAsync(KeyboardHandler.RaritanKeyCode(Windows.System.VirtualKey.Delete) ?? 78, true, ct);
            await Task.Delay(50);
            await SendKeyEventAsync(KeyboardHandler.RaritanKeyCode(Windows.System.VirtualKey.Delete) ?? 78, false, ct);
            await SendKeyEventAsync(KeyboardHandler.RaritanKeyCode(Windows.System.VirtualKey.LeftMenu) ?? 55, false, ct);
            await SendKeyEventAsync(KeyboardHandler.RaritanKeyCode(Windows.System.VirtualKey.LeftControl) ?? 54, false, ct);
        });
    }

    public async Task SendTextAsync(string text, IProgress<(int,int)>? progress = null, CancellationToken ct = default)
    {
        IsSendingText  = true;
        TextSendTotal  = text.Length;
        TextSendProgress = 0;
        var sender = new TextSender(this);
        await sender.SendAsync(text, progress, ct);
        IsSendingText = false;
    }

    // ── Screenshot ────────────────────────────────────────────────────────────

    /// Capture the current framebuffer as a BGRA byte array.
    public byte[]? TakeScreenshot()
    {
        if (Renderer is { } r) return r.CaptureScreenshot();
        if (_framebuffer is null) return null;
        return _framebuffer.AsSpan().ToArray();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FriendlyError(Exception ex) => ex switch
    {
        IOException ioe       => $"Connection error: {ioe.Message}",
        TimeoutException      => "Connection timed out",
        OperationCanceledException => "Disconnected",
        _                     => ex.Message
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
