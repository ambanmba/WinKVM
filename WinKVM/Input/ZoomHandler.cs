namespace WinKVM.Input;

/// Zoom/pan viewport math — all calculations in normalised 0–1 UV space.
public static class ZoomHandler
{
    public const float MinZoom = 1.0f;
    public const float MaxZoom = 8.0f;

    /// Compute display-shader UV parameters from zoom state.
    public static void ComputeZoomRect(float level, float cx, float cy,
                                        out float uvOffX,   out float uvOffY,
                                        out float uvScaleX, out float uvScaleY)
    {
        float scale = 1.0f / level;
        float half  = scale * 0.5f;
        uvOffX   = Math.Clamp(cx - half, 0f, 1f - scale);
        uvOffY   = Math.Clamp(cy - half, 0f, 1f - scale);
        uvScaleX = scale;
        uvScaleY = scale;
    }

    /// Zoom at a normalised cursor position, keeping the point under the cursor fixed.
    public static (float level, float cx, float cy) ApplyDelta(
        float level, float cx, float cy,
        float factor, float atNormX, float atNormY)
    {
        float newLevel = Math.Clamp(level * factor, MinZoom, MaxZoom);

        // UV point under cursor at old zoom
        float uvX = cx + (atNormX - 0.5f) / level;
        float uvY = cy + (atNormY - 0.5f) / level;

        // New center that keeps the same UV under cursor
        float newCx = uvX - (atNormX - 0.5f) / newLevel;
        float newCy = uvY - (atNormY - 0.5f) / newLevel;

        float half = 0.5f / newLevel;
        return (newLevel,
                Math.Clamp(newCx, half, 1f - half),
                Math.Clamp(newCy, half, 1f - half));
    }

    /// Pan the viewport. dNormX/Y are screen-space deltas normalised to [0,1].
    public static (float cx, float cy) Pan(float level, float cx, float cy,
                                           float dNormX, float dNormY)
    {
        float half = 0.5f / level;
        return (Math.Clamp(cx - dNormX / level, half, 1f - half),
                Math.Clamp(cy - dNormY / level, half, 1f - half));
    }

    /// Map renderer pixel coordinates → framebuffer coordinates, accounting for zoom.
    public static (ushort fbX, ushort fbY) MapToFramebuffer(
        double screenX, double screenY,
        double rendW,   double rendH,
        int fbW, int fbH,
        float uvOffX, float uvOffY,
        float uvScaleX, float uvScaleY)
    {
        float fullUvX = uvOffX + (float)(screenX / rendW) * uvScaleX;
        float fullUvY = uvOffY + (float)(screenY / rendH) * uvScaleY;
        return ((ushort)Math.Clamp((int)(fullUvX * fbW), 0, fbW - 1),
                (ushort)Math.Clamp((int)(fullUvY * fbH), 0, fbH - 1));
    }
}
