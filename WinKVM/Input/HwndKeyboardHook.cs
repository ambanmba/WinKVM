using System.Runtime.InteropServices;

namespace WinKVM.Input;

/// Captures keyboard input via WH_KEYBOARD_LL (low-level global hook).
///
/// WinUI 3 routes WM_KEYDOWN to its XAML Island child HWND, not the outer
/// frame, so subclassing the outer window doesn't work.  A global LL hook
/// fires for all keys while the app window is in the foreground, which is
/// exactly what a KVM client needs.
internal sealed class HwndKeyboardHook : IDisposable
{
    private delegate nint LLHookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(
        int idHook, LLHookProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(
        nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint  vkCode;
        public uint  scanCode;
        public uint  flags;
        public uint  time;
        public nuint dwExtraInfo;
    }

    private const int    WH_KEYBOARD_LL = 13;
    private const nint   WM_KEYDOWN     = 0x0100;
    private const nint   WM_KEYUP       = 0x0101;
    private const nint   WM_SYSKEYDOWN  = 0x0104;
    private const nint   WM_SYSKEYUP    = 0x0105;

    private readonly nint       _mainHwnd;
    private readonly nint       _hookHandle;
    private readonly LLHookProc _proc; // keep alive to prevent GC
    private readonly Action<uint, bool> _onKey;

    public HwndKeyboardHook(nint mainHwnd, Action<uint, bool> onKey)
    {
        _mainHwnd   = mainHwnd;
        _onKey      = onKey;
        _proc       = LLProc;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    private static readonly string _log = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "winkvm_kbd2.log");
    private int _logCount;

    private nint LLProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var fg = GetForegroundWindow();
            var kbs = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (_logCount++ < 20)
                File.AppendAllText(_log,
                    $"[{DateTime.Now:HH:mm:ss}] nCode={nCode} wParam=0x{wParam:X} vk=0x{kbs.vkCode:X} fg=0x{fg:X} main=0x{_mainHwnd:X} match={(fg==_mainHwnd)}\n");
            if (fg == _mainHwnd)
            {
                if (wParam is WM_KEYDOWN or WM_SYSKEYDOWN) _onKey(kbs.vkCode, true);
                else if (wParam is WM_KEYUP or WM_SYSKEYUP) _onKey(kbs.vkCode, false);
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle != 0) UnhookWindowsHookEx(_hookHandle);
    }
}
