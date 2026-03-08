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
        _keyboardHook = new HwndKeyboardHook(hwnd, (vk, pressed) =>
        {
            try
            {
                if (page.Session.State == Protocol.SessionState.Connected)
                {
                    var key = (Windows.System.VirtualKey)vk;
                    if (Input.KeyboardHandler.RaritanKeyCode(key) is { } code)
                        _ = page.Session.SendKeyEventAsync(code, pressed);
                }
            }
            catch { /* ignore — must not throw from native callback */ }
        });
    }

    ~MainWindow() => _keyboardHook?.Dispose();
}
