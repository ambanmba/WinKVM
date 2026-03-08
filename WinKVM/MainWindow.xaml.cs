using Microsoft.UI.Xaml;

namespace WinKVM;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
        this.Title = "WinKVM";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
            this.AppWindow.SetIcon(iconPath);
    }
}
