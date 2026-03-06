using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinKVM.Models;

namespace WinKVM.Views;

public sealed partial class LoginPage : UserControl
{
    public event Action<string, ushort, string, string>? ConnectRequested;

    private ProfileStore? _store;
    private List<ConnectionProfile> _profiles = [];

    public LoginPage() => InitializeComponent();

    public void SetProfileStore(ProfileStore store)
    {
        _store = store;
        RefreshProfiles();
        store.Changed += RefreshProfiles;
    }

    private void RefreshProfiles()
    {
        _profiles = [.. _store!.Profiles];
        ProfileCombo.ItemsSource   = _profiles.Select(p => p.Name).ToList();
        ProfileCombo.SelectedIndex = _profiles.Count > 0 ? 0 : -1;
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedIndex < 0 || _store is null) return;
        var p = _profiles[ProfileCombo.SelectedIndex];
        HostBox.Text     = p.Host;
        PortBox.Value    = p.Port;
        UsernameBox.Text = p.Username;
        if (p.SavePassword)
            PasswordBox.Password = _store.LoadPassword(p.Id) ?? "";
    }

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        var host     = HostBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (!ushort.TryParse(PortBox.Value.ToString(), out ushort port)) port = 443;

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username)) return;

        // Save profile if requested
        if (SaveProfileCheck.IsChecked == true && _store is not null)
        {
            var profile = _profiles.FirstOrDefault(p => p.Host == host && p.Username == username)
                        ?? new ConnectionProfile { Name = $"{username}@{host}" };
            profile.Host     = host;
            profile.Port     = port;
            profile.Username = username;
            profile.SavePassword = true;

            if (_profiles.Any(p => p.Id == profile.Id)) _store.Update(profile);
            else _store.Add(profile);
            _store.SavePassword(profile.Id, password);
        }

        ConnectRequested?.Invoke(host, port, username, password);
    }
}
