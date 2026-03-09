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
    private bool              _busy;
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

    public bool IsAvailable => _ready && !_disposed;

    /// Locate the QNN DLL directory on this machine.
    private static string? FindQnnDir()
    {
        var appsRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        if (!System.IO.Directory.Exists(appsRoot)) return null;

        // Prefer the dedicated QNN EP package (most up-to-date)
        foreach (var dir in System.IO.Directory.GetDirectories(appsRoot, "WindowsWorkload.EP.Qualcomm.QNN*"))
        {
            var ep = System.IO.Path.Combine(dir, "ExecutionProvider");
            if (System.IO.File.Exists(System.IO.Path.Combine(ep, "QnnHtp.dll")))
                return ep;
        }
        // Fall back to OnnxRuntime QNN bundle
        foreach (var dir in System.IO.Directory.GetDirectories(appsRoot, "WindowsWorkload.OnnxRuntime.Qnn*"))
        {
            var ep = System.IO.Path.Combine(dir, "ExecutionProvider");
            if (System.IO.File.Exists(System.IO.Path.Combine(ep, "QnnHtp.dll")))
                return ep;
        }
        return null;
    }

    public async Task<bool> InitAsync(int width = 2560, int height = 1440)
    {
        System.IO.File.AppendAllText(_log,
            $"[{DateTime.Now:HH:mm:ss}] QNN InitAsync {width}x{height}\n");
        try
        {
            var onnxBytes = OnnxBuilder.BuildDepthwiseSharpen(3, 0.25f, width, height);
            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] ONNX {onnxBytes.Length} bytes\n");

            // Find QNN DLL directory and add to PATH so ONNX Runtime can load it
            var qnnDir = FindQnnDir();
            System.IO.File.AppendAllText(_log,
                $"[{DateTime.Now:HH:mm:ss}] QNN dir: {qnnDir ?? "not found"}\n");

            var opts = new SessionOptions();
            opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            opts.InterOpNumThreads = 1;
            opts.IntraOpNumThreads = 1;

            string usedProvider = "CPU";
            if (qnnDir is not null)
            {
                var htpPath = System.IO.Path.Combine(qnnDir, "QnnHtp.dll");

                // Add QNN dir to PATH so ONNX Runtime can load dependent QNN DLLs
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!path.Contains(qnnDir))
                    Environment.SetEnvironmentVariable("PATH", qnnDir + ";" + path);

                try
                {
                    opts.AppendExecutionProvider("QNN", new Dictionary<string, string>
                    {
                        { "backend_path",             htpPath },
                        { "device_id",                "0" },
                        { "enable_htp_fp16_precision","1" },
                        { "htp_performance_mode",     "sustained_high_performance" },
                    });
                    usedProvider = "QnnHtp (Hexagon NPU)";
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText(_log,
                        $"[{DateTime.Now:HH:mm:ss}] QnnHtp failed: {ex.Message.Split('\n')[0]}\n");

                    // Try CPU backend as fallback within QNN
                    try
                    {
                        var cpuPath = System.IO.Path.Combine(qnnDir, "QnnCpu.dll");
                        opts.AppendExecutionProvider("QNN", new Dictionary<string, string>
                        {
                            { "backend_path", cpuPath },
                        });
                        usedProvider = "QnnCpu";
                    }
                    catch { /* fall through to ORT CPU */ }
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

    /// Run inference in-place on the display texture: GPU→CPU→QNN→CPU→GPU.
    public async Task EnhanceInPlaceAsync(
        ID3D11DeviceContext ctx, ID3D11Device device,
        ID3D11Texture2D displayTex, int w, int h)
    {
        if (!IsAvailable || _session is null || _busy) return;
        _busy = true;
        try
        {
            EnsureStaging(device, displayTex, w, h);

            // GPU → CPU
            ctx.CopyResource(_stagingRead!, displayTex);
            unsafe
            {
                var m = ctx.Map(_stagingRead!, 0, MapMode.Read);
                PackBgraToNchw((byte*)m.DataPointer, (int)m.RowPitch, _inputBuf, w, h);
                ctx.Unmap(_stagingRead!, 0);
            }

            // QNN / NPU inference
            var inputTensor = new DenseTensor<float>(_inputBuf.AsMemory(),
                                                     new[] { 1, 3, h, w });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor("X", inputTensor) };
            using var results = await Task.Run(() => _session.Run(inputs));
            var outTensor = results.First().AsTensor<float>();
            // Copy tensor data to output buffer
            int idx = 0;
            foreach (var v in outTensor) _outputBuf[idx++] = v;

            // CPU → GPU
            unsafe
            {
                var m = ctx.Map(_stagingWrite!, 0, MapMode.Write);
                UnpackNchwToBgra(_outputBuf, (byte*)m.DataPointer, (int)m.RowPitch, w, h);
                ctx.Unmap(_stagingWrite!, 0);
            }
            ctx.CopyResource(displayTex, _stagingWrite!);

            if (++_inferCount <= 3)
                System.IO.File.AppendAllText(_log,
                    $"[{DateTime.Now:HH:mm:ss}] Inference #{_inferCount} ok\n");
        }
        finally { _busy = false; }
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
