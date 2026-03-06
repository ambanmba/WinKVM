using Microsoft.UI.Input;

namespace WinKVM.Input;

/// Converts WinUI pointer events to e-RIC pointer event parameters.
public static class MouseHandler
{
    /// Build RFB button mask from WinUI pointer point properties.
    /// RFB: bit 0 = left, bit 1 = middle, bit 2 = right.
    public static byte ButtonMask(PointerPointProperties props)
    {
        byte mask = 0;
        if (props.IsLeftButtonPressed)   mask |= 1;
        if (props.IsMiddleButtonPressed) mask |= 2;
        if (props.IsRightButtonPressed)  mask |= 4;
        return mask;
    }

    /// Map view coordinates → framebuffer coordinates (letterboxed).
    public static (ushort x, ushort y) FramebufferCoords(
        double viewX, double viewY,
        double viewW, double viewH,
        int fbWidth,  int fbHeight)
    {
        double scaleX = fbWidth  / viewW;
        double scaleY = fbHeight / viewH;
        int x = Math.Clamp((int)(viewX * scaleX), 0, fbWidth  - 1);
        int y = Math.Clamp((int)(viewY * scaleY), 0, fbHeight - 1);
        return ((ushort)x, (ushort)y);
    }
}
