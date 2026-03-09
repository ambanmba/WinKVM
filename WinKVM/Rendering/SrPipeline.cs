using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Vortice.Direct3D11;
using Windows.AI.MachineLearning;
using Windows.Storage.Streams;

namespace WinKVM.Rendering;

/// NPU-accelerated sharpening via Windows.AI.MachineLearning.
///
/// On Snapdragon X (Copilot+ PC), LearningModelDeviceKind.DirectXMinPower
/// automatically routes inference to the Hexagon NPU — the lowest-power
/// accelerator, which on Snapdragon X is the dedicated AI engine.
///
/// The ONNX model is generated in-memory by OnnxBuilder (no external file).
/// Model: depthwise 3×3 Conv with unsharp-mask kernel, identical semantics
/// to the Adreno GPU CAS pass but running on the Hexagon NPU instead.
public sealed class SrPipeline : IDisposable
{
    private LearningModel?        _model;
    private LearningModelSession? _session;
    private bool                  _ready;
    private bool                  _disposed;
    private bool                  _busy;   // re-entrancy guard
    private int                   _inferCount;

    // Cached read-back and write-back staging textures (avoid per-frame alloc)
    private ID3D11Texture2D? _stagingRead;
    private ID3D11Texture2D? _stagingWrite;
    private int _stagW, _stagH;

    // CPU-side float buffers reused across frames
    private float[] _inputBuf  = [];
    private float[] _outputBuf = [];

    // ITensorNative COM interface for zero-copy output access
    [ComImport, Guid("39d9be93-c6ea-4c5a-ac9d-c7ae17a4e8a8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITensorNative
    {
        int GetBuffer(out nint buffer, out uint capacity);
        int GetD3D12Resource(out nint resource);
    }

    public bool IsAvailable => _ready && !_disposed;

    /// Initialise: build the ONNX model in-memory, load it into WinML,
    /// and route to the Hexagon NPU via DirectXMinPower.
    private static readonly string _log = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "winkvm_npu.log");

    public async Task<bool> InitAsync()
    {
        File.AppendAllText(_log, $"[{DateTime.Now:HH:mm:ss}] InitAsync started\n");
        try
        {
            var onnxBytes = OnnxBuilder.BuildDepthwiseSharpen(3, 0.25f);
            File.AppendAllText(_log, $"[{DateTime.Now:HH:mm:ss}] ONNX built: {onnxBytes.Length} bytes\n");

            // Write to temp file — LoadFromStreamAsync requires a clonable stream
            // which MemoryStream doesn't support; StorageFile path is reliable.
            var tempPath = Path.Combine(Path.GetTempPath(), $"winkvm_sr_{Guid.NewGuid():N}.onnx");
            await File.WriteAllBytesAsync(tempPath, onnxBytes);
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(tempPath);
                _model = await LearningModel.LoadFromStorageFileAsync(file);
            }
            finally { try { File.Delete(tempPath); } catch { } }
            File.AppendAllText(_log, $"[{DateTime.Now:HH:mm:ss}] Model loaded: {_model.Name}\n");

            var device = new LearningModelDevice(LearningModelDeviceKind.DirectXMinPower);
            _session = new LearningModelSession(_model, device);
            File.AppendAllText(_log, $"[{DateTime.Now:HH:mm:ss}] Session created (DirectXMinPower)\n");

            _ready = true;
            return true;
        }
        catch (Exception ex)
        {
            File.AppendAllText(_log, $"[{DateTime.Now:HH:mm:ss}] Init FAILED: {ex.GetType().Name}: {ex.Message}\n");
            return false;
        }
    }

    /// Sharpen the display texture in-place using the Hexagon NPU.
    /// GPU texture → CPU → NPU inference → CPU → GPU texture.
    /// One-frame latency is acceptable for a KVM display path.
    public async Task EnhanceInPlaceAsync(
        ID3D11DeviceContext ctx, ID3D11Device device,
        ID3D11Texture2D displayTex, int w, int h)
    {
        if (!IsAvailable || _session is null || _busy) return;
        _busy = true;
        try
        {
            EnsureStaging(device, displayTex, w, h);

            // ── Readback: GPU → CPU ───────────────────────────────────────────
            ctx.CopyResource(_stagingRead!, displayTex);
            unsafe
            {
                var mapped = ctx.Map(_stagingRead!, 0, MapMode.Read);
                PackBgraToNchw((byte*)mapped.DataPointer, (int)mapped.RowPitch,
                               _inputBuf, w, h);
                ctx.Unmap(_stagingRead!, 0);
            }

            // ── WinML inference on Hexagon NPU ───────────────────────────────
            var inputTensor = TensorFloat.CreateFromArray(new long[] { 1, 3, h, w },
                                                          _inputBuf);
            var binding = new LearningModelBinding(_session);
            binding.Bind("X", inputTensor);
            var result = await _session.EvaluateAsync(binding, "frame");

            // Fast output extraction via ITensorNative (avoids WinRT ABI copy)
            if (result.Outputs["Y"] is TensorFloat outT)
                ReadOutputTensor(outT, _outputBuf);

            // ── Write-back: CPU → GPU ─────────────────────────────────────────
            unsafe
            {
                var mapped = ctx.Map(_stagingWrite!, 0, MapMode.Write);
                UnpackNchwToBgra(_outputBuf, (byte*)mapped.DataPointer,
                                 (int)mapped.RowPitch, w, h);
                ctx.Unmap(_stagingWrite!, 0);
            }
            ctx.CopyResource(displayTex, _stagingWrite!);
        }
        finally { _busy = false; }
    }

    // ── Staging texture management ────────────────────────────────────────────

    private void EnsureStaging(ID3D11Device device, ID3D11Texture2D src, int w, int h)
    {
        if (_stagW == w && _stagH == h) return;

        _stagingRead?.Dispose();  _stagingWrite?.Dispose();

        var desc = src.Description with
        {
            Usage          = ResourceUsage.Staging,
            BindFlags      = BindFlags.None,
            MiscFlags      = ResourceOptionFlags.None,
        };

        desc.CPUAccessFlags = CpuAccessFlags.Read;
        _stagingRead  = device.CreateTexture2D(desc);

        desc.CPUAccessFlags = CpuAccessFlags.Write;
        _stagingWrite = device.CreateTexture2D(desc);

        _inputBuf  = new float[3 * h * w];
        _outputBuf = new float[3 * h * w];
        _stagW = w; _stagH = h;
    }

    // ── Pixel format conversion helpers ──────────────────────────────────────
    // BGRA8 packed → float NCHW (planar RGB order: R=plane0, G=plane1, B=plane2)

    private static unsafe void PackBgraToNchw(
        byte* src, int rowPitch, float[] dst, int w, int h)
    {
        int rOff = 0, gOff = h * w, bOff = 2 * h * w;
        float inv = 1f / 255f;
        for (int y = 0; y < h; y++)
        {
            byte* row = src + (long)y * rowPitch;
            for (int x = 0; x < w; x++)
            {
                dst[bOff + y * w + x] = row[x * 4 + 0] * inv; // B
                dst[gOff + y * w + x] = row[x * 4 + 1] * inv; // G
                dst[rOff + y * w + x] = row[x * 4 + 2] * inv; // R
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
                row[x * 4 + 0] = Clamp(src[bOff + y * w + x]); // B
                row[x * 4 + 1] = Clamp(src[gOff + y * w + x]); // G
                row[x * 4 + 2] = Clamp(src[rOff + y * w + x]); // R
                row[x * 4 + 3] = 255;                            // A
            }
        }
        static byte Clamp(float v) => (byte)(MathF.Min(MathF.Max(v, 0f), 1f) * 255f);
    }

    // Read output tensor. ITensorNative COM QI gives zero-copy access on NPU.
    // Falls back to WinRT IVectorView if QI fails (different WinML driver version).
    private static readonly Guid _iTensorNativeIid =
        new("39d9be93-c6ea-4c5a-ac9d-c7ae17a4e8a8");

    private static void ReadOutputTensor(TensorFloat tensor, float[] dst)
    {
        // Try ITensorNative via COM QueryInterface (zero-copy direct buffer access)
        try
        {
            var unk = Marshal.GetIUnknownForObject(tensor);
            int hr = Marshal.QueryInterface(unk, ref Unsafe.AsRef(_iTensorNativeIid),
                                            out nint nativePtr);
            Marshal.Release(unk);
            if (hr >= 0 && nativePtr != 0)
            {
                var native = (ITensorNative)Marshal.GetObjectForIUnknown(nativePtr);
                Marshal.Release(nativePtr);
                native.GetBuffer(out nint buf, out _);
                unsafe { new ReadOnlySpan<float>((float*)buf, dst.Length).CopyTo(dst); }
                return;
            }
        }
        catch { /* fall through */ }

        // Fallback: WinRT IVectorView (allocates, but always works)
        var view = tensor.GetAsVectorView();
        int n = Math.Min(dst.Length, (int)view.Count);
        for (int i = 0; i < n; i++) dst[i] = view[i];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stagingRead?.Dispose();
        _stagingWrite?.Dispose();
        _session?.Dispose();
        _model = null;
    }
}
