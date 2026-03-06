using Tesseract;

namespace WinKVM.Agent;

/// Tesseract OCR — CPU-based, works offline, good for high-contrast text.
public sealed class TesseractOcr : IOcrProvider
{
    public string Name => "Tesseract";

    private readonly string _dataPath;
    private readonly string _language;

    public TesseractOcr(string language = "eng")
    {
        _language = language;
        _dataPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tessdata");
    }

    public Task<string> RecognizeAsync(byte[] bgraPixels, int width, int height, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var engine = new TesseractEngine(_dataPath, _language, EngineMode.Default);
            using var pix    = Pix.LoadFromMemory(ConvertToRgb(bgraPixels, width, height));
            using var page   = engine.Process(pix);
            return page.GetText();
        }, ct);
    }

    private static byte[] ConvertToRgb(byte[] bgra, int w, int h)
    {
        // BGRA → RGB for Tesseract
        var rgb = new byte[w * h * 3];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
        {
            rgb[j]   = bgra[i + 2]; // R
            rgb[j+1] = bgra[i + 1]; // G
            rgb[j+2] = bgra[i];     // B
        }
        return rgb;
    }
}
