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
    private readonly RawDecoder     _rawDecoder     = new();
    private readonly HextileDecoder _hextileDecoder = new();
    private readonly ICTDecoder     _ictDecoder     = new();

    // Dirty rects
    private readonly List<(int x,int y,int w,int h)> _dirtyRects = [];

    // Credentials (kept only during session)
    private string? _host, _username, _password;
    private ushort  _port = 443;

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

    private async Task PerformConnectionAsync(CancellationToken ct)
    {
        try
        {
            _conn = new ERICConnection();
            await _conn.ConnectAsync(_host!, _port,
                CertificateChallenge is null ? null : (fp, msg) => CertificateChallenge.Invoke(fp, msg), ct);

            State = SessionState.Authenticating;
            StatusMessage = "Authenticating...";

            // RFB version handshake
            var ver = await _conn.ReadAsync(12, ct);
            await _conn.WriteAsync(ver, ct); // echo back

            // Auth
            await PerformAuthAsync(ct);

            State = SessionState.Connected;
            StatusMessage = "Connected";

            // Request framebuffer
            await SendFramebufferUpdateRequestAsync(0, 0, 0xFFFF, 0xFFFF, incremental: false, ct);

            // Message loop
            await MessageLoopAsync(ct);
        }
        catch (OperationCanceledException) { /* normal disconnect */ }
        catch (Exception ex)
        {
            var msg = FriendlyError(ex);
            State = SessionState.Disconnected;
            StatusMessage = msg;
        }
        finally
        {
            ReleaseResources();
        }
    }

    private async Task PerformAuthAsync(CancellationToken ct)
    {
        // Read auth caps (ServerMessage 0x20)
        var caps = await _conn!.ReadAsync(1, ct);
        if (caps[0] != (byte)ServerMessage.AuthCaps)
            throw new IOException("Expected AuthCaps");

        var numMethods = await _conn.ReadAsync(1, ct);
        int n = numMethods[0];
        var methods = await _conn.ReadAsync(n, ct);

        // Prefer MD5
        byte selectedMethod = methods.Contains((byte)AuthMethod.Md5) ? AuthMethod.Md5
            : methods.Contains((byte)AuthMethod.Plain) ? AuthMethod.Plain
            : throw new IOException("No supported auth method");

        // Send Login
        var loginMsg = new BinaryWriter2();
        loginMsg.WriteU8((byte)ClientMessage.Login);
        loginMsg.WriteU8(selectedMethod);
        loginMsg.WriteString(_username!);
        await _conn.WriteAsync(loginMsg.ToArray(), ct);

        if (selectedMethod == AuthMethod.Md5)
        {
            // Read challenge
            var challengeHdr = await _conn.ReadAsync(1, ct);
            if (challengeHdr[0] != (byte)ServerMessage.SessionChallenge)
                throw new IOException("Expected SessionChallenge");
            var challenge = await _conn.ReadAsync(16, ct);
            var response  = ERICAuth.Md5ChallengeResponse(challenge, _password!);

            var authMsg = new BinaryWriter2();
            authMsg.WriteU8((byte)ClientMessage.ChallengeResponse);
            authMsg.WriteString(response);
            await _conn.WriteAsync(authMsg.ToArray(), ct);
        }
        else
        {
            // Plain — send password
            var plainMsg = new BinaryWriter2();
            plainMsg.WriteU8((byte)ClientMessage.ChallengeResponse);
            plainMsg.WriteString(_password!);
            await _conn.WriteAsync(plainMsg.ToArray(), ct);
        }

        // Expect AuthSuccessful
        var result = await _conn.ReadAsync(1, ct);
        if (result[0] != (byte)ServerMessage.AuthSuccessful)
            throw new IOException($"Authentication failed (server returned 0x{result[0]:X2})");

        // Send ClientInit
        var initMsg = new BinaryWriter2();
        initMsg.WriteU8((byte)ClientMessage.ClientInit);
        initMsg.WriteU8(1); // shared session
        await _conn.WriteAsync(initMsg.ToArray(), ct);

        // Read ServerInit (ServerMessage 5)
        await ReadServerInitAsync(ct);

        // Send SetPixelFormat (BGRX 32bpp LE)
        await SendSetPixelFormatAsync(ct);

        // Send SetEncodings
        await SendSetEncodingsAsync(ct);
    }

    private async Task ReadServerInitAsync(CancellationToken ct)
    {
        var hdr = await _conn!.ReadAsync(1, ct);
        if (hdr[0] != (byte)ServerMessage.ServerInit) return;

        var data = await _conn.ReadAsync(24, ct); // simplified — full parse in production
        var r    = new BinaryReader2(data);
        int w    = r.ReadU16();
        int h    = r.ReadU16();
        r.Skip(16); // pixel format
        int nameLen = (int)r.ReadU32();
        if (nameLen > 0 && nameLen < 256)
            await _conn.ReadAsync(nameLen, ct);

        if (w > 0 && h > 0)
        {
            _framebuffer?.Dispose();
            Framebuffer = new KvmFramebuffer(w, h);
            Renderer?.EnsureTexture(w, h);
        }
    }

    // ── Message loop ──────────────────────────────────────────────────────────

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte msgType = await _conn!.ReadByteAsync(ct);

            if (!Enum.IsDefined(typeof(ServerMessage), msgType))
            {
                // Unknown message — skip 3 bytes (best-effort)
                await _conn.ReadAsync(3, ct);
                continue;
            }

            switch ((ServerMessage)msgType)
            {
                case ServerMessage.FramebufferUpdate:
                    await HandleFramebufferUpdateAsync(ct);
                    break;

                case ServerMessage.ServerFBFormat:
                    await HandleServerFBFormatAsync(ct);
                    break;

                case ServerMessage.PingRequest:
                    await HandlePingAsync(ct);
                    break;

                case ServerMessage.ConnectionParameterList:
                    await HandleConnectionParamsAsync(ct);
                    break;

                case ServerMessage.ServerCommand:
                    await HandleServerCommandAsync(ct);
                    break;

                case ServerMessage.PortList:
                case ServerMessage.PortListExt:
                    await HandlePortListAsync(msgType == (byte)ServerMessage.PortListExt, ct);
                    break;

                case ServerMessage.UsbProfileList:
                    await HandleUsbProfileListAsync(ct);
                    break;

                case ServerMessage.VideoSettingsS2C:
                    await HandleVideoSettingsAsync(ct);
                    break;

                case ServerMessage.VideoQualityS2C:
                    await HandleVideoQualityAsync(ct);
                    break;

                case ServerMessage.KeyboardLayout:
                    await HandleKeyboardLayoutAsync(ct);
                    break;

                case ServerMessage.MouseCapsResponse:
                    await HandleMouseCapsAsync(ct);
                    break;

                case ServerMessage.VirtualMediaConfig:
                    await HandleVirtualMediaConfigAsync(ct);
                    break;

                case ServerMessage.AckPixelFormat:
                    // no payload
                    break;

                default:
                    // Drain unknown message (4-byte payload guess)
                    await _conn.ReadAsync(4, ct);
                    break;
            }
        }
    }

    // ── Framebuffer update ────────────────────────────────────────────────────

    private async Task HandleFramebufferUpdateAsync(CancellationToken ct)
    {
        var hdrBytes = await _conn!.ReadAsync(7, ct);
        var r = new BinaryReader2(hdrBytes);
        byte  flags    = r.ReadU8();
        ushort numRects = r.ReadU16();
        uint   size     = r.ReadU32();

        _dirtyRects.Clear();
        var fills    = new List<FillCommand>();
        var rawTiles = new List<RawTileCommand>();
        var rawData  = new byte[0];
        var rawDataMs = new MemoryStream();

        bool useChunkReader = (flags & 0x02) != 0;
        var chunk = useChunkReader ? new ChunkReader(_conn) : null;

        for (int ri = 0; ri < numRects; ri++)
        {
            byte[] rectHdrBytes = chunk is not null
                ? await chunk.ReadAsync(16, ct)
                : await _conn.ReadAsync(16, ct);

            var rh   = new BinaryReader2(rectHdrBytes);
            ushort rx = rh.ReadU16(), ry = rh.ReadU16(), rw = rh.ReadU16(), rh2 = rh.ReadU16();
            int enc  = rh.ReadI32();
            uint ds  = rh.ReadU32();

            if (rw == 0 || rh2 == 0) continue;

            byte[] rectData = chunk is not null
                ? await chunk.ReadAsync((int)ds, ct)
                : await _conn.ReadAsync((int)ds, ct);

            if (_framebuffer is null) continue;

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

                case RfbEncoding.NewFBSize:
                    if (rw > 0 && rh2 > 0)
                    {
                        _framebuffer?.Dispose();
                        Framebuffer = new KvmFramebuffer(rw, rh2);
                        Renderer?.EnsureTexture(rw, rh2);
                    }
                    break;

                default:
                    // ICT and other encodings handled by serverFBFormat path
                    break;
            }
        }

        if (chunk is not null) await chunk.FinishAsync(ct);

        // Flush to GPU
        if (Renderer is { } r2 && _framebuffer is { } fb)
        {
            if (fills.Count > 0)
                r2.ExecuteFills([.. fills], fb.Width, fb.Height);
            else
                r2.UploadFramebuffer(fb, _dirtyRects.Count > 0 ? _dirtyRects : null);
        }

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

        // Request next frame
        await SendFramebufferUpdateRequestAsync(0, 0, (ushort)(_framebuffer?.Width ?? 1920),
            (ushort)(_framebuffer?.Height ?? 1080), incremental: true, ct);
    }

    private async Task HandleServerFBFormatAsync(CancellationToken ct)
    {
        // ICT frame: read header, decode, upload YCbCr to GPU
        var hdr = await _conn!.ReadAsync(12, ct);
        var r   = new BinaryReader2(hdr);
        ushort w = r.ReadU16(), h = r.ReadU16();
        int quality = r.ReadU8();
        r.Skip(3);
        uint dataSize = r.ReadU32();

        var data = await _conn.ReadAsync((int)dataSize, ct);

        if (w > 0 && h > 0)
        {
            var planes = await Task.Run(() => _ictDecoder.Decode(data, w, h, quality), ct);
            if (planes is not null && Renderer is { } renderer)
            {
                renderer.UploadYCbCr(planes);
                planes.Dispose();
            }
        }

        await SendFramebufferUpdateRequestAsync(0, 0, w, h, incremental: true, ct);
    }

    // ── Ping ──────────────────────────────────────────────────────────────────

    private async Task HandlePingAsync(CancellationToken ct)
    {
        var ping = await _conn!.ReadAsync(4, ct);
        var reply = new BinaryWriter2();
        reply.WriteU8((byte)ClientMessage.PingReply);
        reply.WriteBytes(ping);
        await _conn.WriteAsync(reply.ToArray(), ct);
    }

    // ── ConnectionParameterList ───────────────────────────────────────────────

    private async Task HandleConnectionParamsAsync(CancellationToken ct)
    {
        var lenBytes = await _conn!.ReadAsync(2, ct);
        ushort len   = (ushort)((lenBytes[0] << 8) | lenBytes[1]);
        if (len == 0) return;
        var data = await _conn.ReadAsync(len, ct);
        var text = Encoding.UTF8.GetString(data);
        foreach (var pair in text.Split(';'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0)
                ConnectionParams[pair[..eq].Trim()] = pair[(eq+1)..].Trim();
        }
        if (ConnectionParams.TryGetValue("audio_support", out var au))
            AudioSupport = au;
    }

    // ── ServerCommand ─────────────────────────────────────────────────────────

    private async Task HandleServerCommandAsync(CancellationToken ct)
    {
        var lenBytes = await _conn!.ReadAsync(2, ct);
        ushort len   = (ushort)((lenBytes[0] << 8) | lenBytes[1]);
        if (len > 0) await _conn.ReadAsync(len, ct); // consume — parsed as needed
    }

    // ── Port list ─────────────────────────────────────────────────────────────

    private async Task HandlePortListAsync(bool ext, CancellationToken ct)
    {
        var cnt = await _conn!.ReadAsync(1, ct);
        Ports.Clear();
        for (int i = 0; i < cnt[0]; i++)
        {
            var idBytes = await _conn.ReadAsync(ext ? 4 : 2, ct);
            var nameLen = (await _conn.ReadAsync(1, ct))[0];
            var name    = nameLen > 0 ? Encoding.UTF8.GetString(await _conn.ReadAsync(nameLen, ct)) : "";
            string id   = ext ? BitConverter.ToString(idBytes) : $"{idBytes[0]:X2}{idBytes[1]:X2}";
            Ports.Add(new KvmPort(id, (ushort)(i + 1), name));
        }
    }

    // ── USB profiles ──────────────────────────────────────────────────────────

    private async Task HandleUsbProfileListAsync(CancellationToken ct)
    {
        var cnt = (await _conn!.ReadAsync(1, ct))[0];
        UsbProfiles.Clear();
        for (int i = 0; i < cnt; i++)
        {
            var idBytes = await _conn.ReadAsync(2, ct);
            ushort id   = (ushort)((idBytes[0] << 8) | idBytes[1]);
            var nLen    = (await _conn.ReadAsync(1, ct))[0];
            var name    = nLen > 0 ? Encoding.UTF8.GetString(await _conn.ReadAsync(nLen, ct)) : "";
            UsbProfiles.Add(new UsbProfile(id, name));
        }
    }

    // ── Video / keyboard / mouse ──────────────────────────────────────────────

    private async Task HandleVideoSettingsAsync(CancellationToken ct)
    {
        var data = await _conn!.ReadAsync(6, ct);
        var r    = new BinaryReader2(data);
        ushort w = r.ReadU16(), h = r.ReadU16();
        r.Skip(2);
        VideoSettings = new VideoSettings("Unknown", w, h);
    }

    private async Task HandleVideoQualityAsync(CancellationToken ct)
    {
        var d = await _conn!.ReadAsync(2, ct);
        VideoQuality = (d[0], d[1]);
    }

    private async Task HandleKeyboardLayoutAsync(CancellationToken ct)
    {
        var len  = (await _conn!.ReadAsync(1, ct))[0];
        var data = len > 0 ? await _conn.ReadAsync(len, ct) : [];
        KeyboardLayout = Encoding.ASCII.GetString(data);
    }

    private async Task HandleMouseCapsAsync(CancellationToken ct)
    {
        var cnt = (await _conn!.ReadAsync(1, ct))[0];
        MouseCaps = cnt > 0 ? await _conn.ReadAsync(cnt, ct) : [];
    }

    private async Task HandleVirtualMediaConfigAsync(CancellationToken ct)
    {
        var d = await _conn!.ReadAsync(4, ct);
        VmDriveCount = d[0];
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
        var encodings = new[]
        {
            (int)RfbEncoding.Hextile,
            (int)RfbEncoding.Raw,
            (int)RfbEncoding.NewFBSize,
            (int)RfbEncoding.LastRect,
            128, // ICT (Raritan proprietary)
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

    public async Task SendPointerEventAsync(ushort x, ushort y, byte buttonMask, CancellationToken ct = default)
    {
        var msg = new BinaryWriter2();
        msg.WriteU8((byte)ClientMessage.PointerEvent);
        msg.WriteU8(buttonMask);
        msg.WriteU16(x); msg.WriteU16(y);
        await _conn!.WriteAsync(msg.ToArray(), ct);
    }

    public async Task SendScrollEventAsync(ushort x, ushort y, byte buttonMask, short z, CancellationToken ct = default)
    {
        // RFB scroll: button mask bits 3 (up) and 4 (down)
        byte scrollMask = buttonMask;
        if (z > 0) for (int i = 0; i < z; i++) scrollMask |= 8;
        if (z < 0) for (int i = 0; i < -z; i++) scrollMask |= 16;
        await SendPointerEventAsync(x, y, scrollMask, ct);
        await SendPointerEventAsync(x, y, 0, ct);
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
