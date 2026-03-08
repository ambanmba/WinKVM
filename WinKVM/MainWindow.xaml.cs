using Microsoft.UI.Xaml;
using WinKVM.Input;
using WinRT.Interop;

namespace WinKVM;

public sealed partial class MainWindow : Window
{
    private HwndKeyboardHook? _keyboardHook;

    public MainWindow()
    {
        this.InitializeComponent();
        this.Title = "WinKVM";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
            this.AppWindow.SetIcon(iconPath);

        // Install Win32 keyboard hook after window is created.
        // Fires for all WM_KEYDOWN/KEYUP regardless of XAML focus state.
        Activated += OnFirstActivated;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        var hwnd = WindowNative.GetWindowHandle(this);
        var page = (Views.MainPage)((Microsoft.UI.Xaml.Controls.Grid)Content).Children[0];
        var kbdLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "winkvm_kbd.log");
        int kbdLogCount = 0;
        _keyboardHook = new HwndKeyboardHook(hwnd, (vk, pressed) =>
        {
            if (page.Session.State == Protocol.SessionState.Connected)
            {
                var key = (Windows.System.VirtualKey)vk;
                if (Input.KeyboardHandler.RaritanKeyCode(key) is { } code)
                {
                    if (kbdLogCount++ < 5)
                        File.AppendAllText(kbdLog, $"[{DateTime.Now:HH:mm:ss}] SEND vk=0x{vk:X} code={code} pressed={pressed}\n");
                    _ = page.Session.SendKeyEventAsync(code, pressed);
                }
                else if (kbdLogCount++ < 5)
                    File.AppendAllText(kbdLog, $"[{DateTime.Now:HH:mm:ss}] NO_MAP vk=0x{vk:X} pressed={pressed}\n");
            }
            else if (kbdLogCount++ < 3)
                File.AppendAllText(kbdLog, $"[{DateTime.Now:HH:mm:ss}] NOT_CONNECTED vk=0x{vk:X} state={page.Session.State}\n");
        });
    }

    ~MainWindow() => _keyboardHook?.Dispose();
}
