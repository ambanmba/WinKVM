using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.Direct3D11;

namespace WinKVM.Rendering;

/// Super-resolution pipeline for the zoom viewport — 3-phase design keeps
/// all D3D11 calls on the render thread; ONNX inference runs on thread pool.
///
/// Phase 1 (render thread): PrepareStagingRead  — CopyResource + Map + crop NCHW
/// Phase 2 (thread pool):   RunInferenceAsync   — ONNX → _outputBuf
/// Phase 3 (render thread): CommitToTexture     — unpack + CopyResource → dstTex
///
/// To enable: drop a real ONNX SR model as "sr_model.onnx" next to the .exe.
/// Expected tensor contract:
///   Input  (first input)  : float32 [1, 3, inH,  inW ]  — RGB 0..1 NCHW
///   Output (first output) : float32 [1, 3, outH, outW]  — RGB 0..1 NCHW
/// inW/inH are read from the model; outW/outH must equal display resolution.
public sealed class SrZoomPipeline : IDisposable
{
    // Calling a static member of SrPipeline forces its static constructor to run first,
    // which pre-loads the QNN-paired ORT DLLs and sets NativeLibrary.SetDllImportResolver.
    static SrZoomPipeline() { SrPipeline.EnsureQnnBootstrapped(); }

    private InferenceSession? _session;
    private bool              _ready;
    private bool              _disposed;

    // CPU pixel buffers (resized to model input/output dimensions)
    private float[] _inputBuf  = [];
    private float[] _outputBuf = [];

    // Model input/output dimensions (detected from ONNX metadata)
    private int _inW, _inH, _outW, _outH;

    // Staging textures (created lazily on first use)
    private ID3D11Texture2D? _stagingRead;   // full display size, CpuRead
    private ID3D11Texture2D? _stagingWrite;  // output size, CpuWrite
    private int _stagSrcW, _stagSrcH;

    public static readonly string ModelPath =
        Path.Combine(AppContext.BaseDirectory, "sr_model.onnx");

    private static readonly string _log = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "winkvm_sr_zoom.log");

    public bool IsAvailable => _ready && !_disposed;
    public int OutW => _outW;
    public int OutH => _outH;

    // ── Init ──────────────────────────────────────────────────────────────────

    /// Loads sr_model.onnx. Returns false (no crash) if the file is absent.
    public async Task<bool> InitAsync(int displayW, int displayH)
    {
        if (!File.Exists(ModelPath))
        {
            Log($"No SR model at {ModelPath} — zoom uses Lanczos only");
            return false;
        }
        Log($"Loading SR model: {ModelPath}");
        try
        {
            var opts = new SessionOptions();
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            // Try Hexagon NPU (HTP) first — same backend as per-frame colour enhancement.
            // FP16 mode lets the NPU run float32 models by auto-converting to FP16.
            // Falls back through QNN GPU → CPU automatically on unsupported ops.
            var (qnnEpDir, qnnOrtDir) = FindQnnDirs();
            if (qnnEpDir is not null && qnnOrtDir is not null)
            {
                // Add both dirs to PATH: qnnOrtDir has onnxruntime_providers_qnn.dll,
                // qnnEpDir has QnnHtp.dll. Must be first so Windows finds these before NuGet copies.
                var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var d in new[] { qnnOrtDir, qnnEpDir })
                    if (!envPath.Contains(d)) envPath = d + ";" + envPath;
                Environment.SetEnvironmentVariable("PATH", envPath);
                try
                {
                    opts.AppendExecutionProvider("QNN", new Dictionary<string, string>
                    {
                        { "backend_path",                             "QnnHtp.dll" },
                        { "device_id",                               "0" },
                        { "enable_htp_fp16_precision",               "1" },
                        { "htp_performance_mode",                    "burst" },
                        { "htp_graph_finalization_optimization_mode","3" },
                        { "soc_model",                               "60" },
                    });
                    Log("SR: QNN HTP (Hexagon NPU) EP registered");
                }
                catch (Exception ex) { Log($"SR: QNN HTP unavailable: {ex.Message.Split('\n')[0]}"); }
            }

            _session = await Task.Run(() => new InferenceSession(ModelPath, opts));

            // Detect input size from model metadata
            var inDims  = _session.InputMetadata.Values.First().Dimensions;
            var outDims = _session.OutputMetadata.Values.First().Dimensions;

            // Dynamic dim (-1) → fall back to display / 4
            _inW  = inDims  is [_, _, _, var w1] && w1 > 0 ? (int)w1 : displayW / 4;
            _inH  = inDims  is [_, _, var h1, _] && h1 > 0 ? (int)h1 : displayH / 4;
            _outW = outDims is [_, _, _, var w2] && w2 > 0 ? (int)w2 : displayW;
            _outH = outDims is [_, _, var h2, _] && h2 > 0 ? (int)h2 : displayH;

            _inputBuf  = new float[3 * _inH  * _inW];
            _outputBuf = new float[3 * _outH * _outW];
            _ready = true;
            Log($"SR ready: in={_inW}x{_inH} out={_outW}x{_outH}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"SR load failed: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    // ── Phase 1: capture + crop (render thread) ───────────────────────────────

    /// Copy the full display texture to a staging buffer and crop the zoom
    /// sub-region into _inputBuf at model input resolution.
    /// Returns false if the GPU was not ready (caller should skip this frame).
    public bool PrepareStagingRead(
        ID3D11DeviceContext ctx, ID3D11Device device,
        ID3D11Texture2D srcTex, int srcW, int srcH,
        float uvOffX, float uvOffY, float uvSclX, float uvSclY)
    {
        EnsureStaging(device, srcTex, srcW, srcH);
        ctx.CopyResource(_stagingRead!, srcTex);
        try
        {
            unsafe
            {
                var m = ctx.Map(_stagingRead!, 0, MapMode.Read);
                CropBgraToNchw((byte*)m.DataPointer, (int)m.RowPitch,
                               _inputBuf, srcW, srcH,
                               uvOffX, uvOffY, uvSclX, uvSclY,
                               _inW, _inH);
                ctx.Unmap(_stagingRead!, 0);
                return true;
            }
        }
        catch (SharpGen.Runtime.SharpGenException) { return false; }
    }

    // ── Phase 2: ONNX inference (thread pool) ─────────────────────────────────

    public async Task RunInferenceAsync()
    {
        if (_session is null) return;
        var inName  = _session.InputNames[0];
        var tensor  = new DenseTensor<float>(_inputBuf.AsMemory(), [1, 3, _inH, _inW]);
        var inputs  = new[] { NamedOnnxValue.CreateFromTensor(inName, tensor) };
        using var results = await Task.Run(() => _session.Run(inputs));
        var outT = results.First().AsTensor<float>();
        int i = 0;
        foreach (var v in outT) _outputBuf[i++] = v;
    }

    // ── Phase 3: write result (render thread) ─────────────────────────────────

    public void CommitToTexture(ID3D11DeviceContext ctx, ID3D11Texture2D dstTex)
    {
        unsafe
        {
            var m = ctx.Map(_stagingWrite!, 0, MapMode.Write);
            UnpackNchwToBgra(_outputBuf, (byte*)m.DataPointer, (int)m.RowPitch, _outW, _outH);
            ctx.Unmap(_stagingWrite!, 0);
        }
        ctx.CopyResource(dstTex, _stagingWrite!);
    }

    // ── Pixel helpers ─────────────────────────────────────────────────────────

    private static unsafe void CropBgraToNchw(
        byte* src, int rowPitch,
        float[] dst, int srcW, int srcH,
        float uvOffX, float uvOffY, float uvSclX, float uvSclY,
        int outW, int outH)
    {
        float inv = 1f / 255f;
        int rOff = 0, gOff = outH * outW, bOff = 2 * outH * outW;
        for (int dy = 0; dy < outH; dy++)
        {
            float v  = uvOffY + (dy + 0.5f) / outH * uvSclY;
            int   sY = Math.Clamp((int)(v * srcH), 0, srcH - 1);
            byte* row = src + (long)sY * rowPitch;
            for (int dx = 0; dx < outW; dx++)
            {
                float u  = uvOffX + (dx + 0.5f) / outW * uvSclX;
                int   sX = Math.Clamp((int)(u * srcW), 0, srcW - 1) * 4;
                dst[bOff + dy * outW + dx] = row[sX + 0] * inv;
                dst[gOff + dy * outW + dx] = row[sX + 1] * inv;
                dst[rOff + dy * outW + dx] = row[sX + 2] * inv;
            }
        }
    }

    private static unsafe void UnpackNchwToBgra(
        float[] src, byte* dst, int rowPitch, int w, int h)
    {
        int rOff = 0, gOff = h * w, bOff = 2 * h * w;
        for (int y = 0; y < h; y++)
        {
            byte* row = dst + (long)y * rowPitch;
            for (int x = 0; x < w; x++)
            {
                row[x * 4 + 0] = Clamp(src[bOff + y * w + x]);
                row[x * 4 + 1] = Clamp(src[gOff + y * w + x]);
                row[x * 4 + 2] = Clamp(src[rOff + y * w + x]);
                row[x * 4 + 3] = 255;
            }
        }
        static byte Clamp(float v) => (byte)(MathF.Min(MathF.Max(v, 0f), 1f) * 255f);
    }

    // ── Staging management ────────────────────────────────────────────────────

    private void EnsureStaging(ID3D11Device device, ID3D11Texture2D src, int srcW, int srcH)
    {
        if (_stagingRead is not null && _stagSrcW == srcW && _stagSrcH == srcH) return;
        _stagingRead?.Dispose(); _stagingWrite?.Dispose();

        var d = src.Description with
        {
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        };
        _stagingRead = device.CreateTexture2D(d);

        // Write staging at SR output resolution
        d.Width = (uint)_outW; d.Height = (uint)_outH;
        d.CPUAccessFlags = CpuAccessFlags.Write;
        _stagingWrite = device.CreateTexture2D(d);

        _stagSrcW = srcW; _stagSrcH = srcH;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stagingRead?.Dispose(); _stagingWrite?.Dispose();
        _session?.Dispose(); _session = null;
    }

    private void Log(string msg) =>
        File.AppendAllText(_log, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");

    private static (string? epDir, string? ortDir) FindQnnDirs()
    {
        var appsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (!Directory.Exists(appsRoot)) return (null, null);
        string? epDir = null, ortDir = null;
        foreach (var d in Directory.GetDirectories(appsRoot, "WindowsWorkload.EP.Qualcomm.QNN*"))
        {
            var ep = Path.Combine(d, "ExecutionProvider");
            if (File.Exists(Path.Combine(ep, "QnnHtp.dll"))) { epDir = ep; break; }
        }
        foreach (var d in Directory.GetDirectories(appsRoot, "WindowsWorkload.OnnxRuntime.Qnn*"))
        {
            if (File.Exists(Path.Combine(d, "onnxruntime.dll")) &&
                File.Exists(Path.Combine(d, "onnxruntime_providers_qnn.dll")))
            { ortDir = d; break; }
        }
        return (epDir, ortDir);
    }
}
