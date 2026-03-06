using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WinKVM.Agent;

/// Windows.Media.Ocr — NPU-accelerated on Copilot+ PCs (Snapdragon X, AMD Ryzen AI, Intel Core Ultra).
/// Falls back to CPU on non-NPU hardware. Zero extra API cost; fully on-device.
public sealed class WindowsOcr : IOcrProvider
{
    public string Name => "Windows OCR (NPU)";

    private readonly OcrEngine _engine;

    public WindowsOcr()
    {
        // Use the user's current UI language; fall back to English
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
               ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en"))
               ?? throw new InvalidOperationException("Windows OCR engine unavailable");
    }

    public async Task<string> RecognizeAsync(byte[] bgraPixels, int width, int height, CancellationToken ct = default)
    {
        // Convert BGRA→BGRA8 SoftwareBitmap
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(
            bgraPixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width, height,
            BitmapAlphaMode.Premultiplied);

        var result = await _engine.RecognizeAsync(bmp).AsTask(ct);

        var sb = new System.Text.StringBuilder();
        foreach (var line in result.Lines)
        {
            sb.AppendLine(line.Text);
        }
        return sb.ToString();
    }
}
