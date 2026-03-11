using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.Direct3D11;

namespace WinKVM.Rendering;

/// Super-resolution pipeline for the zoom viewport.
///
/// Drop a real SR ONNX model (e.g. Real-ESRGAN-General-x4v3) into the app
/// directory as "sr_model.onnx" and this pipeline will use it automatically.
///
/// Without a model file it falls back gracefully (zoom uses Lanczos only).
///
/// GPU → CPU → NPU/CPU → CPU → GPU roundtrip is acceptable here because SR
/// only runs when the zoom region changes (user interaction), not every frame.
///
/// When a real model is present the expected tensor contract is:
///   Input  "input"  : float32[1, 3, H_in,  W_in ]  — RGB, 0..1, NCHW
///   Output "output" : float32[1, 3, H_out, W_out]   — RGB, 0..1, NCHW
/// where H_out = H_in * scale, W_out = W_in * scale (e.g. 4×).
public sealed class SrZoomPipeline : IDisposable
{
    // ── State ─────────────────────────────────────────────────────────────────
    private InferenceSession? _session;
    private bool              _ready;
    private bool              _disposed;

    // CPU pixel buffers
    private float[] _inputBuf  = [];
    private float[] _outputBuf = [];
    private int _inW, _inH, _outW, _outH;

    // D3D staging textures
    private ID3D11Texture2D? _stagingRead;
    private ID3D11Texture2D? _stagingWrite;

    // Model location — placed next to the executable
    public static readonly string ModelPath = System.IO.Path.Combine(
        AppContext.BaseDirectory, "sr_model.onnx");

    private static readonly string _log = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "winkvm_sr_zoom.log");

    public bool IsAvailable => _ready && !_disposed;

    // ── Init ──────────────────────────────────────────────────────────────────

    public async Task<bool> InitAsync(int inW, int inH, int outW, int outH)
    {
        if (!System.IO.File.Exists(ModelPath))
        {
            Log($"No SR model at {ModelPath} — falling back to Lanczos-only zoom");
            return false;
        }

        Log($"Loading SR model: {ModelPath}  in={inW}x{inH}  out={outW}x{outH}");
        try
        {
            var opts = new SessionOptions();
            opts.ExecutionMode    = ExecutionMode.ORT_SEQUENTIAL;
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            // Try QNN GPU backend (Adreno, zero-copy with shared buffers on some versions)
            // Falls back through DirectML → CPU automatically.
            try
            {
                opts.AppendExecutionProvider("QNN", new Dictionary<string, string>
                {
                    { "backend_path", "QnnGpu.dll" },  // Adreno GPU via QNN
                    { "device_id",    "0" },
                });
                Log("SR: QNN GPU EP registered");
            }
            catch { /* QnnGpu not available — ORT will use CPU */ }

            _session = await Task.Run(() => new InferenceSession(ModelPath, opts));

            _inW  = inW;  _inH  = inH;
            _outW = outW; _outH = outH;
            _inputBuf  = new float[3 * inH  * inW];
            _outputBuf = new float[3 * outH * outW];
            _ready = true;
            Log("SR model loaded OK");
            return true;
        }
        catch (Exception ex)
        {
            Log($"SR model load failed: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    // ── Inference ─────────────────────────────────────────────────────────────

    /// Upscale the current zoom crop from srcTex and write result to dstTex.
    /// srcTex is sampled according to the UV zoom rect; dstTex is the display-
    /// resolution output that feeds directly into the display pass.
    public async Task EnhanceZoomAsync(
        ID3D11DeviceContext ctx, ID3D11Device device,
        ID3D11Texture2D srcTex, ID3D11Texture2D dstTex,
        float uvOffX, float uvOffY, float uvScaleX, float uvScaleY,
        int srcW, int srcH, int dstW, int dstH)
    {
        if (!IsAvailable || _session is null) return;

        EnsureStaging(device, srcTex, dstW, dstH);

        // GPU → CPU (read the zoom crop from the source texture)
        ctx.CopyResource(_stagingRead!, srcTex);
        unsafe
        {
            var m = ctx.Map(_stagingRead!, 0, MapMode.Read);
            CropBgraToNchw((byte*)m.DataPointer, (int)m.RowPitch,
                           _inputBuf, srcW, srcH,
                           uvOffX, uvOffY, uvScaleX, uvScaleY,
                           _inW, _inH);
            ctx.Unmap(_stagingRead!, 0);
        }

        // ONNX inference (CPU or QNN GPU)
        var inputName  = _session.InputNames[0];
        var outputName = _session.OutputNames[0];
        var tensor     = new DenseTensor<float>(_inputBuf.AsMemory(), [1, 3, _inH, _inW]);
        var inputs     = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

        using var results = await Task.Run(() => _session.Run(inputs));
        var outTensor = results.First().AsTensor<float>();
        int idx = 0;
        foreach (var v in outTensor) _outputBuf[idx++] = v;

        // CPU → GPU (write SR result to destination texture)
        unsafe
        {
            var m = ctx.Map(_stagingWrite!, 0, MapMode.Write);
            UnpackNchwToBgra(_outputBuf, (byte*)m.DataPointer, (int)m.RowPitch, _outW, _outH);
            ctx.Unmap(_stagingWrite!, 0);
        }
        ctx.CopyResource(dstTex, _stagingWrite!);
    }

    // ── Pixel helpers ─────────────────────────────────────────────────────────

    /// Crop the zoom sub-rect from the full-res staging texture and pack to NCHW float.
    private static unsafe void CropBgraToNchw(
        byte* src, int rowPitch,
        float[] dst, int srcW, int srcH,
        float uvOffX, float uvOffY, float uvScaleX, float uvScaleY,
        int outW, int outH)
    {
        float inv = 1f / 255f;
        int rOff = 0, gOff = outH * outW, bOff = 2 * outH * outW;

        for (int dy = 0; dy < outH; dy++)
        {
            float v   = uvOffY + (dy + 0.5f) / outH * uvScaleY;
            int   sY  = Math.Clamp((int)(v * srcH), 0, srcH - 1);
            byte* row = src + (long)sY * rowPitch;

            for (int dx = 0; dx < outW; dx++)
            {
                float u  = uvOffX + (dx + 0.5f) / outW * uvScaleX;
                int   sX = Math.Clamp((int)(u * srcW), 0, srcW - 1);

                dst[bOff + dy * outW + dx] = row[sX * 4 + 0] * inv; // B
                dst[gOff + dy * outW + dx] = row[sX * 4 + 1] * inv; // G
                dst[rOff + dy * outW + dx] = row[sX * 4 + 2] * inv; // R
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

    // ── Staging texture management ────────────────────────────────────────────

    private void EnsureStaging(ID3D11Device device, ID3D11Texture2D src, int dstW, int dstH)
    {
        if (_stagingRead is not null) return; // already created
        var d = src.Description with
        {
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        d.CPUAccessFlags = CpuAccessFlags.Read;  _stagingRead  = device.CreateTexture2D(d);
        // Write staging at output resolution
        d.Width          = (uint)dstW;
        d.Height         = (uint)dstH;
        d.CPUAccessFlags = CpuAccessFlags.Write; _stagingWrite = device.CreateTexture2D(d);
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
        System.IO.File.AppendAllText(_log, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
}
