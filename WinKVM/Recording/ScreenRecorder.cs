using System.Diagnostics;
using System.Runtime.InteropServices;
using WinKVM.Framebuffer;

namespace WinKVM.Recording;

/// Encodes KVM video frames + RAP audio to an H.264/AAC MP4 file using
/// the Windows MediaFoundation Sink Writer.
///
/// Video path: YCbCrPlanes (from ICT decoder) → NV12 (direct copy, very cheap)
///             or BGRA framebuffer → NV12 (converted on CPU for hextile/raw frames)
/// Audio path: PCM bytes from RAPConnection.AudioPacketReceived → AAC via MF encoder
///
/// All COM object pointers are released on Dispose/StopRecording.
/// Thread-safe: AddVideoFrame / AddAudioSamples may be called from different threads.
public sealed class ScreenRecorder : IDisposable
{
    // ── MediaFoundation GUIDs ─────────────────────────────────────────────────

    static readonly Guid MFMediaType_Video              = G("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid MFMediaType_Audio              = G("73647561-0000-0010-8000-00AA00389B71");
    static readonly Guid MFVideoFormat_H264             = G("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid MFVideoFormat_NV12             = G("3231564E-0000-0010-8000-00AA00389B71");
    static readonly Guid MFAudioFormat_PCM              = G("00000001-0000-0010-8000-00AA00389B71");
    static readonly Guid MFAudioFormat_AAC              = G("00001610-0000-0010-8000-00AA00389B71");
    static readonly Guid MF_MT_MAJOR_TYPE               = G("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MF_MT_SUBTYPE                  = G("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MF_MT_FRAME_SIZE               = G("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_FRAME_RATE               = G("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid MF_MT_PIXEL_ASPECT_RATIO       = G("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid MF_MT_INTERLACE_MODE           = G("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid MF_MT_AVG_BITRATE              = G("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = G("5faeeae7-0290-4c31-9e8a-c534f68d9eda");
    static readonly Guid MF_MT_AUDIO_NUM_CHANNELS       = G("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE    = G("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = G("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT    = G("322de230-9eeb-43bd-ab7a-ff412251541d");

    static Guid G(string s) => new(s);

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport("mfplat.dll")]    static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")]    static extern int MFShutdown();
    [DllImport("mfplat.dll")]    static extern int MFCreateMediaType(out IntPtr ppMFType);
    [DllImport("mfplat.dll")]    static extern int MFCreateSample(out IntPtr ppSample);
    [DllImport("mfplat.dll")]    static extern int MFCreateMemoryBuffer(uint cbMax, out IntPtr ppBuffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    static extern int MFCreateSinkWriterFromURL(
        string url, IntPtr pByteStream, IntPtr pAttributes, out IntPtr ppSinkWriter);

    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr _sw;              // IMFSinkWriter*
    private uint   _vidIdx, _audIdx;
    private long   _vidTime, _audTime;         // 100-ns units
    private long   _lastFrameTick = -1;
    private long   _frameInterval;             // target 100-ns per frame (fallback)
    private bool   _recording;
    private bool   _disposed;
    private int    _fbW, _fbH;
    private int    _audSampleRate = 44100, _audChannels = 2, _audBits = 16;
    private byte[] _nv12 = [];

    private readonly object _lock = new();

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsRecording => _recording;

    /// Start recording to an MP4 file.
    public bool Start(string outputPath, int width, int height,
                      int targetFps = 30, int videoBitrate = 8_000_000,
                      int audioBitrate = 128_000)
    {
        lock (_lock)
        {
            if (_recording || width <= 0 || height <= 0) return false;
            // Width/height must be even for NV12
            width  = width  & ~1;
            height = height & ~1;

            int hr = MFStartup(0x00020070, 0); // MF_VERSION=2.0, MFSTARTUP_NOSOCKET=0
            if (hr < 0) return false;

            hr = MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, IntPtr.Zero, out _sw);
            if (hr < 0 || _sw == IntPtr.Zero) { MFShutdown(); return false; }

            _fbW = width; _fbH = height;
            _frameInterval = 10_000_000L / targetFps;
            _nv12 = new byte[width * height * 3 / 2];

            // ── Video output type (H264) ──────────────────────────────────────
            MFCreateMediaType(out var vOut);
            MT_SetGUID(vOut, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            MT_SetGUID(vOut, MF_MT_SUBTYPE,    MFVideoFormat_H264);
            MT_SetU32( vOut, MF_MT_AVG_BITRATE, (uint)videoBitrate);
            MT_SetU64( vOut, MF_MT_FRAME_SIZE,  Pack(width, height));
            MT_SetU64( vOut, MF_MT_FRAME_RATE,  Pack(targetFps, 1));
            MT_SetU64( vOut, MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            MT_SetU32( vOut, MF_MT_INTERLACE_MODE, 2); // Progressive
            SW_AddStream(_sw, vOut, out _vidIdx);
            Release(vOut);

            // ── Video input type (NV12) ───────────────────────────────────────
            MFCreateMediaType(out var vIn);
            MT_SetGUID(vIn, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            MT_SetGUID(vIn, MF_MT_SUBTYPE,    MFVideoFormat_NV12);
            MT_SetU64( vIn, MF_MT_FRAME_SIZE, Pack(width, height));
            MT_SetU64( vIn, MF_MT_FRAME_RATE, Pack(targetFps, 1));
            MT_SetU64( vIn, MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            MT_SetU32( vIn, MF_MT_INTERLACE_MODE, 2);
            SW_SetInput(_sw, _vidIdx, vIn);
            Release(vIn);

            // ── Audio output type (AAC) ───────────────────────────────────────
            MFCreateMediaType(out var aOut);
            MT_SetGUID(aOut, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            MT_SetGUID(aOut, MF_MT_SUBTYPE,    MFAudioFormat_AAC);
            MT_SetU32( aOut, MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)_audSampleRate);
            MT_SetU32( aOut, MF_MT_AUDIO_NUM_CHANNELS,       (uint)_audChannels);
            MT_SetU32( aOut, MF_MT_AUDIO_BITS_PER_SAMPLE,    (uint)_audBits);
            MT_SetU32( aOut, MF_MT_AVG_BITRATE,              (uint)audioBitrate);
            SW_AddStream(_sw, aOut, out _audIdx);
            Release(aOut);

            // ── Audio input type (PCM) ────────────────────────────────────────
            int blockAlign = _audBits / 8 * _audChannels;
            MFCreateMediaType(out var aIn);
            MT_SetGUID(aIn, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            MT_SetGUID(aIn, MF_MT_SUBTYPE,    MFAudioFormat_PCM);
            MT_SetU32( aIn, MF_MT_AUDIO_SAMPLES_PER_SECOND,  (uint)_audSampleRate);
            MT_SetU32( aIn, MF_MT_AUDIO_NUM_CHANNELS,         (uint)_audChannels);
            MT_SetU32( aIn, MF_MT_AUDIO_BITS_PER_SAMPLE,      (uint)_audBits);
            MT_SetU32( aIn, MF_MT_AUDIO_BLOCK_ALIGNMENT,      (uint)blockAlign);
            MT_SetU32( aIn, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(_audSampleRate * blockAlign));
            SW_SetInput(_sw, _audIdx, aIn);
            Release(aIn);

            SW_BeginWriting(_sw);
            _vidTime = 0; _audTime = 0; _lastFrameTick = -1;
            _recording = true;
            return true;
        }
    }

    /// Feed a YCbCrPlanes frame (direct from ICT decoder — very cheap, no BGRA conversion).
    public unsafe void AddVideoFrameYCbCr(YCbCrPlanes planes)
    {
        if (!_recording) return;
        int w = planes.Width & ~1, h = planes.Height & ~1;
        if (w != _fbW || h != _fbH) return; // size mismatch — skip

        lock (_lock)
        {
            if (!_recording) return;
            fixed (byte* pNv12 = _nv12)
            {
                // Y plane: planes.Y is already unmanaged byte* from NativeMemory
                byte* pY = (byte*)planes.Y;
                for (int row = 0; row < h; row++)
                    Buffer.MemoryCopy(pY + row * planes.YStride, pNv12 + row * w, w, w);

                // UV plane: interleave Cb/Cr (YUV 4:2:0 → NV12)
                int uvBase = w * h;
                int cwSrc  = planes.CStride;
                for (int row = 0; row < h / 2; row++)
                {
                    byte* cbRow = (byte*)planes.Cb + row * cwSrc;
                    byte* crRow = (byte*)planes.Cr + row * cwSrc;
                    byte* dst   = pNv12 + uvBase + row * w;
                    for (int x = 0; x < w / 2; x++)
                    {
                        dst[x * 2]     = cbRow[x];
                        dst[x * 2 + 1] = crRow[x];
                    }
                }
            }
            WriteSampleNV12(_nv12);
        }
    }

    /// Feed a BGRA framebuffer frame (hextile/raw path).
    public unsafe void AddVideoFrameBGRA(KvmFramebuffer fb)
    {
        if (!_recording) return;
        int w = fb.Width & ~1, h = fb.Height & ~1;
        if (w != _fbW || h != _fbH) return;

        lock (_lock)
        {
            if (!_recording) return;
            fixed (byte* pNv12 = _nv12)
            {
                // Y plane
                for (int y = 0; y < h; y++)
                {
                    byte* row = fb.RowPointer(y);
                    for (int x = 0; x < w; x++)
                    {
                        int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        pNv12[y * w + x] = (byte)Math.Clamp(16 + ((66 * r + 129 * g + 25 * b) >> 8), 0, 255);
                    }
                }
                // UV plane
                int uvBase = w * h;
                for (int y = 0; y < h / 2; y++)
                {
                    byte* row = fb.RowPointer(y * 2);
                    for (int x = 0; x < w / 2; x++)
                    {
                        int b = row[x * 8], g = row[x * 8 + 1], r = row[x * 8 + 2];
                        pNv12[uvBase + y * w + x * 2]     = (byte)Math.Clamp(128 + ((-38 * r - 74 * g + 112 * b) >> 8), 0, 255);
                        pNv12[uvBase + y * w + x * 2 + 1] = (byte)Math.Clamp(128 + ((112 * r - 94 * g - 18 * b) >> 8), 0, 255);
                    }
                }
            }
            WriteSampleNV12(_nv12);
        }
    }

    /// Feed PCM audio from the RAP stream. May be called from any thread.
    public void AddAudioSamples(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        if (!_recording) return;
        lock (_lock)
        {
            if (!_recording || _sw == IntPtr.Zero) return;
            int bytesPerSample = bitsPerSample / 8 * channels;
            int numSamples = pcm.Length / bytesPerSample;
            long duration  = (long)numSamples * 10_000_000L / sampleRate;
            WriteRawSample(_audIdx, pcm, _audTime, duration);
            _audTime += duration;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_recording || _sw == IntPtr.Zero) return;
            _recording = false;
            SW_Finalize(_sw);
            Release(_sw);
            _sw = IntPtr.Zero;
            MFShutdown();
        }
    }

    public void Dispose() { if (!_disposed) { _disposed = true; Stop(); } }

    // ── Internal frame writing ────────────────────────────────────────────────

    private void WriteSampleNV12(byte[] nv12)
    {
        if (_sw == IntPtr.Zero) return;
        long now = Stopwatch.GetTimestamp();
        long elapsed = _lastFrameTick < 0 ? _frameInterval
                     : (now - _lastFrameTick) * 10_000_000L / Stopwatch.Frequency;
        elapsed = Math.Clamp(elapsed, _frameInterval / 4, _frameInterval * 4);
        _lastFrameTick = now;
        WriteRawSample(_vidIdx, nv12, _vidTime, elapsed);
        _vidTime += elapsed;
    }

    private void WriteRawSample(uint streamIdx, byte[] data, long time, long duration)
    {
        if (_sw == IntPtr.Zero) return;
        MFCreateMemoryBuffer((uint)data.Length, out var buf);
        if (buf == IntPtr.Zero) return;
        try
        {
            MB_Lock(buf, out var ptr, out _, out _);
            Marshal.Copy(data, 0, ptr, data.Length);
            MB_Unlock(buf);
            MB_SetCurrentLength(buf, (uint)data.Length);

            MFCreateSample(out var smp);
            if (smp == IntPtr.Zero) { Release(buf); return; }
            try
            {
                S_AddBuffer(smp, buf);
                S_SetSampleTime(smp, time);
                S_SetSampleDuration(smp, duration);
                SW_WriteSample(_sw, streamIdx, smp);
            }
            finally { Release(smp); }
        }
        finally { Release(buf); }
    }

    // ── COM vtable helpers ────────────────────────────────────────────────────
    // All COM objects are IntPtr (IUnknown*). Vtable is the first pointer-sized
    // field. Methods are indexed from 0 (QueryInterface).

    // IMFAttributes vtable indices:
    //   21: SetUINT32  22: SetUINT64  24: SetGUID
    static unsafe void MT_SetGUID(IntPtr obj, Guid key, Guid val)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)(GetVt(obj, 24));
        fn(obj, &key, &val);
    }
    static unsafe void MT_SetU32(IntPtr obj, Guid key, uint val)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)(GetVt(obj, 21));
        fn(obj, &key, val);
    }
    static unsafe void MT_SetU64(IntPtr obj, Guid key, ulong val)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)(GetVt(obj, 22));
        fn(obj, &key, val);
    }

    // IMFSinkWriter vtable indices (inherits IUnknown directly):
    //   3: AddStream  4: SetInputMediaType  5: BeginWriting  6: WriteSample  11: Finalize
    static unsafe void SW_AddStream(IntPtr sw, IntPtr mt, out uint idx)
    {
        fixed (uint* p = &idx)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)(GetVt(sw, 3));
            fn(sw, mt, p);
        }
    }
    static unsafe void SW_SetInput(IntPtr sw, uint idx, IntPtr mt)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)(GetVt(sw, 4));
        fn(sw, idx, mt, IntPtr.Zero);
    }
    static unsafe void SW_BeginWriting(IntPtr sw)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)(GetVt(sw, 5));
        fn(sw);
    }
    static unsafe void SW_WriteSample(IntPtr sw, uint idx, IntPtr smp)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)(GetVt(sw, 6));
        fn(sw, idx, smp);
    }
    static unsafe void SW_Finalize(IntPtr sw)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)(GetVt(sw, 11));
        fn(sw);
    }

    // IMFSample vtable indices (inherits 33 from IMFAttributes):
    //   36: SetSampleTime  38: SetSampleDuration  42: AddBuffer
    static unsafe void S_AddBuffer(IntPtr smp, IntPtr buf)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(GetVt(smp, 42));
        fn(smp, buf);
    }
    static unsafe void S_SetSampleTime(IntPtr smp, long t)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)(GetVt(smp, 36));
        fn(smp, t);
    }
    static unsafe void S_SetSampleDuration(IntPtr smp, long d)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)(GetVt(smp, 38));
        fn(smp, d);
    }

    // IMFMediaBuffer vtable indices (inherits IUnknown):
    //   3: Lock  4: Unlock  6: SetCurrentLength
    static unsafe void MB_Lock(IntPtr buf, out IntPtr pData, out uint maxLen, out uint curLen)
    {
        fixed (IntPtr* pp = &pData) fixed (uint* pm = &maxLen) fixed (uint* pc = &curLen)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, int>)(GetVt(buf, 3));
            fn(buf, pp, pm, pc);
        }
    }
    static unsafe void MB_Unlock(IntPtr buf)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)(GetVt(buf, 4));
        fn(buf);
    }
    static unsafe void MB_SetCurrentLength(IntPtr buf, uint len)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)(GetVt(buf, 6));
        fn(buf, len);
    }

    static unsafe void* GetVt(IntPtr obj, int idx) =>
        ((void**)(*(void**)obj))[idx];

    static unsafe void Release(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(GetVt(obj, 2));
        fn(obj);
    }

    static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;
}
