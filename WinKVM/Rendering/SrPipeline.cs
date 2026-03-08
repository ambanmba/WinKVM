using System.Runtime.InteropServices.WindowsRuntime;
using Windows.AI.MachineLearning;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Storage;

namespace WinKVM.Rendering;

/// Windows.AI.MachineLearning super-resolution pipeline.
///
/// On Snapdragon X (Copilot+ PC), LearningModelSession with
/// LearningModelDeviceKind.DirectXHighPerformance automatically routes
/// compute to the Hexagon NPU or best available accelerator.
///
/// Requires an ONNX SR model at: Assets/sr_model.onnx
/// Suitable models (2× or 4× upscale, or same-resolution artifact removal):
///   • https://github.com/onnx/models/tree/main/validated/vision/super_resolution
///   • Real-ESRGAN (tile-based, 256×256 tiles)
///   • DnCNN (JPEG artifact removal, same resolution)
///
/// Drop the ONNX file into WinKVM/Assets/ and set Build Action = Content.
public sealed class SrPipeline : IDisposable
{
    private LearningModel?        _model;
    private LearningModelSession? _session;
    private bool                  _ready;
    private bool                  _disposed;

    private string _inputName  = "input";
    private string _outputName = "output";

    public bool IsAvailable => _ready && !_disposed;

    /// Load the ONNX model asynchronously. Call once at startup.
    /// Returns true if the model was found and loaded successfully.
    public async Task<bool> InitAsync()
    {
        try
        {
            var modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "sr_model.onnx");
            if (!File.Exists(modelPath)) return false;

            var file  = await StorageFile.GetFileFromPathAsync(modelPath);
            _model    = await LearningModel.LoadFromStorageFileAsync(file);

            // DirectXHighPerformance → NPU on Snapdragon X / Copilot+ PCs
            var device = new LearningModelDevice(LearningModelDeviceKind.DirectXHighPerformance);
            _session   = new LearningModelSession(_model, device);

            if (_model.InputFeatures.Count > 0)
                _inputName  = _model.InputFeatures[0].Name;
            if (_model.OutputFeatures.Count > 0)
                _outputName = _model.OutputFeatures[0].Name;

            _ready = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SrPipeline] Load failed: {ex.Message}");
            return false;
        }
    }

    /// Enhance a frame. Input and output are raw BGRA byte arrays (width × height × 4).
    /// Returns null if the pipeline is not ready or fails.
    public async Task<byte[]?> EnhanceAsync(byte[] bgra, int width, int height)
    {
        if (!IsAvailable || _session is null) return null;
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height);

            var binding = new LearningModelBinding(_session);
            binding.Bind(_inputName, ImageFeatureValue.CreateFromVideoFrame(
                VideoFrame.CreateWithSoftwareBitmap(bitmap)));

            var result = await _session.EvaluateAsync(binding, Guid.NewGuid().ToString());

            var outputImage = result.Outputs[_outputName] as ImageFeatureValue;
            if (outputImage?.VideoFrame.SoftwareBitmap is not { } outBitmap) return null;

            using var converted = SoftwareBitmap.Convert(outBitmap, BitmapPixelFormat.Bgra8);
            int outSize = converted.PixelWidth * converted.PixelHeight * 4;
            var outBytes = new byte[outSize];
            converted.CopyToBuffer(outBytes.AsBuffer());
            return outBytes;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SrPipeline] Enhance failed: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _model  = null;
        _session = null;
    }
}
