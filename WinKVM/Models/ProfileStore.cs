using System.Text.Json;
using Windows.Storage;

namespace WinKVM.Models;

/// Persists connection profiles to ApplicationData LocalSettings (JSON).
public sealed class ProfileStore
{
    private const string ProfilesKey   = "ConnectionProfiles";
    private const string PasswordPrefix = "kvm_pw_";

    private List<ConnectionProfile> _profiles = new();

    public IReadOnlyList<ConnectionProfile> Profiles => _profiles;

    public event Action? Changed;

    public ProfileStore() => Load();

    // ── Persistence ─────────────────────────────────────────────────────────

    private void Load()
    {
        var settings = ApplicationData.Current.LocalSettings;
        if (settings.Values[ProfilesKey] is string json)
        {
            try { _profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? new(); }
            catch { _profiles = new(); }
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_profiles);
        ApplicationData.Current.LocalSettings.Values[ProfilesKey] = json;
    }

    // ── CRUD ────────────────────────────────────────────────────────────────

    public void Add(ConnectionProfile profile)
    {
        _profiles.Add(profile);
        Save();
        Changed?.Invoke();
    }

    public void Update(ConnectionProfile profile)
    {
        var idx = _profiles.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0) { _profiles[idx] = profile; Save(); Changed?.Invoke(); }
    }

    public void Remove(Guid id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        DeletePassword(id);
        Save();
        Changed?.Invoke();
    }

    // ── Password storage via Windows Credential Manager ─────────────────────

    public void SavePassword(Guid id, string password)
    {
        var key = PasswordPrefix + id.ToString("N");
        ApplicationData.Current.LocalSettings.Values[key] = password;
    }

    public string? LoadPassword(Guid id)
    {
        var key = PasswordPrefix + id.ToString("N");
        return ApplicationData.Current.LocalSettings.Values[key] as string;
    }

    private void DeletePassword(Guid id)
    {
        var key = PasswordPrefix + id.ToString("N");
        ApplicationData.Current.LocalSettings.Values.Remove(key);
    }
}
