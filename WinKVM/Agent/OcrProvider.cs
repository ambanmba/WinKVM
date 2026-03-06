using System.Drawing;

namespace WinKVM.Agent;

public interface IOcrProvider
{
    string Name { get; }
    /// Perform OCR on a BGRA pixel buffer. Returns recognised text.
    Task<string> RecognizeAsync(byte[] bgraPixels, int width, int height, CancellationToken ct = default);
}

/// OCR result element with bounding box and text.
public record OcrElement(string Text, RectangleF Bounds, string? ElementType = null);
