using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.Direct3D11;

namespace WinKVM.Rendering;

/// Hexagon NPU sharpening via ONNX Runtime + QNN execution provider.
///
/// Uses Microsoft.ML.OnnxRuntime with AppendExecutionProvider("QNN") to route
/// inference to the Hexagon HTP (Tensor Processor) on Snapdragon X.
/// The QNN DLLs ship with Windows 11 ARM64 at:
///   %ProgramFiles%\WindowsApps\WindowsWorkload.EP.Qualcomm.QNN.*\ExecutionProvider\
///
/// Falls back through: QnnHtp → QnnCpu → OnnxRuntime CPU in that order.
public sealed class SrPipeline : IDisposable
{
    private InferenceSession? _session;
    private bool              _ready;
    private bool              _disposed;
    private int               _inferCount;

    // CPU-side float buffers reused across frames
    private float[] _inputBuf  = [];
    private float[] _outputBuf = [];

    // Cached read/write staging textures
    private ID3D11Texture2D? _stagingRead;
    private ID3D11Texture2D? _stagingWrite;
    private int _stagW, _stagH;

    private static readonly string _log = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "winkvm_npu.log");

    // Static constructor: pre-load ALL paired DLLs from the WindowsApps QNN ORT bundle
    // before anything else can load the NuGet's incompatible versions.
    static SrPipeline()
    {
        var (_, ortDir) = FindQnnDirs();
        if (ortDir is null) return;
        try
        {
            // Load onnxruntime.dll + onnxruntime_providers_shared.dll from the QNN bundle.
            // Once in memory, Windows finds these (by name) before the NuGet copies.
            var ortHandle    = NativeLibrary.Load(System.IO.Path.Combine(ortDir, "onnxruntime.dll"));
            var sharedPath   = System.IO.Path.Combine(ortDir, "onnxruntime_providers_shared.dll");
            var sharedHandle = System.IO.File.Exists(sharedPath)
                             ? NativeLibrary.Load(sharedPath) : IntPtr.Zero;

            // Redirect all P/Invoke calls so the ORT C# wrapper uses these handles
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly,
                (libName, _, _) =>
                    libName == "onnxruntime"                  ? ortHandle    :
                    libName == "onnxruntime_providers_shared" ? sharedHandle :
                    IntPtr.Zero);

            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] Static: pre-loaded QNN-paired ORT DLLs from {ortDir}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] Static init failed: {ex.Message.Split('\n')[0]}\n");
        }
    }

    public bool IsAvailable => _ready && !_disposed;

    /// Locate the QNN directories: returns (qnnEpDir, qnnOrtDir) where:
    ///   qnnEpDir  = directory containing QnnHtp.dll (for backend_path)
    ///   qnnOrtDir = directory containing onnxruntime.dll + onnxruntime_providers_qnn.dll
    ///               (the paired ORT build that was compiled with QNN EP support)
    private static (string? epDir, string? ortDir) FindQnnDirs()
    {
        var appsRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        if (!System.IO.Directory.Exists(appsRoot)) return (null, null);

        string? epDir = null, ortDir = null;

        // QNN EP DLLs (QnnHtp.dll etc.)
        foreach (var dir in System.IO.Directory.GetDirectories(appsRoot, "WindowsWorkload.EP.Qualcomm.QNN*"))
        {
            var ep = System.IO.Path.Combine(dir, "ExecutionProvider");
            if (System.IO.File.Exists(System.IO.Path.Combine(ep, "QnnHtp.dll")))
            { epDir = ep; break; }
        }

        // OnnxRuntime build paired with QNN EP — has BOTH onnxruntime.dll AND onnxruntime_providers_qnn.dll
        foreach (var dir in System.IO.Directory.GetDirectories(appsRoot, "WindowsWorkload.OnnxRuntime.Qnn*"))
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "onnxruntime.dll")) &&
                System.IO.File.Exists(System.IO.Path.Combine(dir, "onnxruntime_providers_qnn.dll")))
            { ortDir = dir; break; }
        }

        return (epDir, ortDir);
    }

    public async Task<bool> InitAsync(int width = 2560, int height = 1440)
    {
        System.IO.File.AppendAllText(_log,
            $"[{DateTime.Now:HH:mm:ss}] QNN InitAsync {width}x{height}\n");
        try
        {
            var onnxBytes = OnnxBuilder.BuildDepthwiseSharpen(3, 0.3f, width, height);
            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] ONNX {onnxBytes.Length} bytes\n");

            var (qnnEpDir, qnnOrtDir) = FindQnnDirs();
            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] EP dir: {qnnEpDir ?? "n/a"}\n" +
                $"[{DateTime.Now:HH:mm:ss}] ORT dir: {qnnOrtDir ?? "n/a"}\n");

            var opts = new SessionOptions();
            opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            opts.InterOpNumThreads = 1;
            opts.IntraOpNumThreads = 1;

            string usedProvider = "CPU";
            if (qnnEpDir is not null && qnnOrtDir is not null)
            {
                var htpPath  = System.IO.Path.Combine(qnnEpDir, "QnnHtp.dll");
                var qnnEpDll = System.IO.Path.Combine(qnnOrtDir, "onnxruntime_providers_qnn.dll");

                // Add qnnOrtDir FIRST — contains QnnHtp.dll matched to onnxruntime_providers_qnn.dll
                var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var d in new[] { qnnOrtDir, qnnEpDir })
                    if (!envPath.Contains(d)) envPath = d + ";" + envPath;
                Environment.SetEnvironmentVariable("PATH", envPath);

                try
                {
                    opts.AppendExecutionProvider("QNN", new Dictionary<string, string>
                    {
                        { "backend_path",                              "QnnHtp.dll" },
                        { "device_id",                                 "0" },
                        { "enable_htp_fp16_precision",                "1" },
                        { "htp_performance_mode",                      "sustained_high_performance" },
                        { "htp_graph_finalization_optimization_mode",  "3" },
                        { "soc_model",                                 "60" },
                    });
                    usedProvider = "QnnHtp (Hexagon NPU)";
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText(_log,
                        $"[{DateTime.Now:HH:mm:ss}] QnnHtp failed: {ex.Message.Split('\n')[0]}\n");
                }
            }

            // Create session (runs on thread pool — avoids UI thread blocking)
            _session = await Task.Run(() => new InferenceSession(onnxBytes, opts));
            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] Session on {usedProvider}\n");

            _inputBuf  = new float[3 * height * width];
            _outputBuf = new float[3 * height * width];
            _ready = true;
            return true;
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] Init FAILED: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}\n");
            return false;
        }
    }

    /// Phase 1 — capture current frame into CPU buffer.
    /// Must be called on the render (D3D11) thread.
    public bool PrepareStagingRead(ID3D11DeviceContext ctx, ID3D11Device device,
                                   ID3D11Texture2D srcTex, int w, int h)
    {
        EnsureStaging(device, srcTex, w, h);
        ctx.CopyResource(_stagingRead!, srcTex);
        unsafe
        {
            var m = ctx.Map(_stagingRead!, 0, MapMode.Read);
            PackBgraToNchw((byte*)m.DataPointer, (int)m.RowPitch, _inputBuf, w, h);
            ctx.Unmap(_stagingRead!, 0);
            return true;
        }
    }

    /// Phase 2 — run ONNX inference on thread pool (no D3D11 calls).
    public async Task RunInferenceAsync(int w, int h)
    {
        if (_session is null) return;
        var inputTensor = new DenseTensor<float>(_inputBuf.AsMemory(), new[] { 1, 3, h, w });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("X", inputTensor) };
        using var results = await Task.Run(() => _session.Run(inputs));
        var outTensor = results.First().AsTensor<float>();
        int idx = 0;
        foreach (var v in outTensor) _outputBuf[idx++] = v;
        if (++_inferCount <= 5)
            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] Inference #{_inferCount} ok\n");
    }

    /// Phase 3 — write inference result to dstTex.
    /// Must be called on the render (D3D11) thread.
    public void CommitToTexture(ID3D11DeviceContext ctx, ID3D11Texture2D dstTex, int w, int h)
    {
        unsafe
        {
            var m = ctx.Map(_stagingWrite!, 0, MapMode.Write);
            UnpackNchwToBgra(_outputBuf, (byte*)m.DataPointer, (int)m.RowPitch, w, h);
            ctx.Unmap(_stagingWrite!, 0);
        }
        ctx.CopyResource(dstTex, _stagingWrite!);
    }

    // ── Staging texture management ────────────────────────────────────────────

    private void EnsureStaging(ID3D11Device device, ID3D11Texture2D src, int w, int h)
    {
        if (_stagW == w && _stagH == h) return;
        _stagingRead?.Dispose(); _stagingWrite?.Dispose();
        var d = src.Description with { Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                                       MiscFlags = ResourceOptionFlags.None };
        d.CPUAccessFlags = CpuAccessFlags.Read;  _stagingRead  = device.CreateTexture2D(d);
        d.CPUAccessFlags = CpuAccessFlags.Write; _stagingWrite = device.CreateTexture2D(d);
        _stagW = w; _stagH = h;
    }

    // ── Pixel conversion ──────────────────────────────────────────────────────

    private static unsafe void PackBgraToNchw(
        byte* src, int rowPitch, float[] dst, int w, int h)
    {
        float inv = 1f / 255f;
        int rOff = 0, gOff = h * w, bOff = 2 * h * w;
        for (int y = 0; y < h; y++)
        {
            byte* row = src + (long)y * rowPitch;
            for (int x = 0; x < w; x++)
            {
                dst[bOff + y * w + x] = row[x * 4 + 0] * inv;
                dst[gOff + y * w + x] = row[x * 4 + 1] * inv;
                dst[rOff + y * w + x] = row[x * 4 + 2] * inv;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stagingRead?.Dispose(); _stagingWrite?.Dispose();
        _session?.Dispose();
        _session = null;
    }
}
