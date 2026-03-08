using Microsoft.UI.Xaml;

namespace WinKVM;

public partial class App : Application
{
    private Window? _window;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "WinKVM_crash.log");

    public App()
    {
        this.UnhandledException += (_, e) =>
        {
            File.AppendAllText(LogPath, $"[UnhandledException] {e.Exception}\n");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            File.AppendAllText(LogPath, $"[AppDomain] {e.ExceptionObject}\n");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            File.AppendAllText(LogPath, $"[Task] {e.Exception}\n");

        try
        {
            this.InitializeComponent();
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[InitializeComponent] {ex}\n");
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[OnLaunched] {ex}\n");
            throw;
        }
    }
}
