using Microsoft.UI.Xaml;

namespace WinKVM;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
        this.Title = "WinKVM";
        this.ExtendsContentIntoTitleBar = true;
        this.AppWindow.SetIcon("Assets/AppIcon.ico");
    }
}
