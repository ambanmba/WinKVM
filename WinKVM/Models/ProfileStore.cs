using System.Text.Json;

namespace WinKVM.Models;

/// Persists connection profiles to %LOCALAPPDATA%\WinKVM\ (JSON files).
/// Uses plain file I/O so the app can run unpackaged (no MSIX identity needed).
public sealed class ProfileStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinKVM");
    private static readonly string ProfilesFile = Path.Combine(DataDir, "profiles.json");
    private static readonly string PasswordsFile = Path.Combine(DataDir, "passwords.json");

    private List<ConnectionProfile> _profiles = new();
    private Dictionary<string, string> _passwords = new();

    public IReadOnlyList<ConnectionProfile> Profiles => _profiles;

    public event Action? Changed;

    public ProfileStore() => Load();

    // ── Persistence ─────────────────────────────────────────────────────────

    private void Load()
    {
        Directory.CreateDirectory(DataDir);
        if (File.Exists(ProfilesFile))
        {
            try { _profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(ProfilesFile)) ?? new(); }
            catch { _profiles = new(); }
        }
        if (File.Exists(PasswordsFile))
        {
            try { _passwords = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PasswordsFile)) ?? new(); }
            catch { _passwords = new(); }
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(_profiles));
    }

    private void SavePasswords()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(PasswordsFile, JsonSerializer.Serialize(_passwords));
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

    // ── Password storage ─────────────────────────────────────────────────────

    public void SavePassword(Guid id, string password)
    {
        _passwords[id.ToString("N")] = password;
        SavePasswords();
    }

    public string? LoadPassword(Guid id) =>
        _passwords.TryGetValue(id.ToString("N"), out var pw) ? pw : null;

    private void DeletePassword(Guid id)
    {
        _passwords.Remove(id.ToString("N"));
        SavePasswords();
    }
}
