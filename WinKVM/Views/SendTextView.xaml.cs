using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinKVM.Protocol;

namespace WinKVM.Views;

public sealed partial class SendTextView : UserControl
{
    public ERICSession? Session { get; set; }

    public SendTextView() => InitializeComponent();

    private async void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Session is null || string.IsNullOrEmpty(TextInput.Text)) return;

        SendProgress.Visibility = Visibility.Visible;
        var progress = new Progress<(int sent, int total)>(p =>
            SendProgress.Value = (double)p.sent / p.total * 100);

        await Session.SendTextAsync(TextInput.Text, progress);

        SendProgress.Visibility = Visibility.Collapsed;
        SendProgress.Value      = 0;
        TextInput.Text = "";
    }
}
