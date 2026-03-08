using System.Runtime.InteropServices;

namespace WinKVM.Input;

/// Captures WM_KEYDOWN/KEYUP by subclassing the app HWND via SetWindowSubclass.
/// Reliable regardless of WinUI 3 XAML focus state — the standard approach for
/// KVM-style applications where all keyboard input must reach the remote host.
internal sealed class HwndKeyboardHook : IDisposable
{
    private delegate nint SubclassProc(
        nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(
        nint hWnd, uint uMsg, nint wParam, nint lParam);

    private const uint WM_KEYDOWN    = 0x0100;
    private const uint WM_KEYUP      = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP   = 0x0105;
    private const nuint SubclassId   = 0x574B564D; // "WKVM"

    private readonly nint       _hwnd;
    private readonly SubclassProc _proc; // keep delegate alive — prevents GC
    private readonly Action<uint, bool> _onKey;

    public HwndKeyboardHook(nint hwnd, Action<uint, bool> onKey)
    {
        _hwnd  = hwnd;
        _onKey = onKey;
        _proc  = WndProc;
        SetWindowSubclass(hwnd, _proc, SubclassId, 0);
    }

    private nint WndProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint _, nuint __)
    {
        if (uMsg is WM_KEYDOWN or WM_SYSKEYDOWN) _onKey((uint)wParam, true);
        else if (uMsg is WM_KEYUP  or WM_SYSKEYUP)   _onKey((uint)wParam, false);
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose() => RemoveWindowSubclass(_hwnd, _proc, SubclassId);
}
