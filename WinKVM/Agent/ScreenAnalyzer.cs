using System.Drawing;
using System.Text;

namespace WinKVM.Agent;

/// Analyses a framebuffer screenshot using OCR and produces a structured
/// screen description for the AI agent system prompt.
public static class ScreenAnalyzer
{
    public record AnalysisResult(string Description, IReadOnlyList<OcrElement> Elements);

    public static async Task<AnalysisResult> AnalyzeAsync(
        byte[] bgraPixels, int width, int height,
        IOcrProvider ocrProvider,
        CancellationToken ct = default)
    {
        var rawText = await ocrProvider.RecognizeAsync(bgraPixels, width, height, ct);
        var lines   = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Build a simplified description without OCR bounding boxes (Windows OCR line objects
        // don't expose per-word bounding boxes in the same way as Apple Vision).
        var sb = new StringBuilder();

        // Divide screen into regions by Y coordinate
        int topBarH    = (int)(height * 0.08);
        int bottomBarY = (int)(height * 0.92);

        var topLines    = new List<string>();
        var mainLines   = new List<string>();
        var bottomLines = new List<string>();

        // Without bounding boxes, just partition by line index (approximate)
        int thirds = Math.Max(1, lines.Length / 3);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i < thirds / 3)          topLines.Add(lines[i]);
            else if (i >= lines.Length - thirds / 3) bottomLines.Add(lines[i]);
            else                          mainLines.Add(lines[i]);
        }

        if (topLines.Count > 0)
        {
            sb.AppendLine("TOP BAR:");
            foreach (var l in topLines) sb.AppendLine($"  {l}");
        }
        if (mainLines.Count > 0)
        {
            sb.AppendLine("MAIN:");
            foreach (var l in mainLines) sb.AppendLine($"  {l}");
        }
        if (bottomLines.Count > 0)
        {
            sb.AppendLine("BOTTOM BAR:");
            foreach (var l in bottomLines) sb.AppendLine($"  {l}");
        }

        var elements = lines.Select(l => new OcrElement(l, RectangleF.Empty)).ToList();
        return new AnalysisResult(sb.ToString(), elements);
    }
}
