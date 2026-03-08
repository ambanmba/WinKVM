using System.Security.Cryptography;
using System.Text.Json;

namespace WinKVM.Protocol;

/// Discriminated union for certificate check results.
public abstract record CertCheckResult
{
    public sealed record Trusted : CertCheckResult;
    public sealed record FirstUse(string Fingerprint) : CertCheckResult;
    public sealed record Changed(string OldFingerprint, string NewFingerprint) : CertCheckResult;
}

/// Trust-on-First-Use certificate store backed by a JSON file in %LOCALAPPDATA%\WinKVM\.
/// Uses plain file I/O so the app can run unpackaged (no MSIX identity needed).
public static class CertificateStore
{
    private static readonly string StoreFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKVM", "trusted_certs.json");

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(StoreFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StoreFile)) ?? new();
        }
        catch { }
        return new();
    }

    private static void Save(Dictionary<string, string> store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(store));
        }
        catch { }
    }

    /// Compute a SHA-256 fingerprint as colon-separated hex (matches Swift format).
    public static string Fingerprint(byte[] derData)
    {
        var hash = SHA256.HashData(derData);
        return string.Join(":", hash.Select(b => b.ToString("X2")));
    }

    public static CertCheckResult Check(string host, ushort port, string fingerprint)
    {
        var key   = $"{host}:{port}";
        var store = Load();
        if (!store.TryGetValue(key, out var stored))
            return new CertCheckResult.FirstUse(fingerprint);

        return stored == fingerprint
            ? new CertCheckResult.Trusted()
            : new CertCheckResult.Changed(stored, fingerprint);
    }

    public static void Trust(string host, ushort port, string fingerprint)
    {
        var store = Load();
        store[$"{host}:{port}"] = fingerprint;
        Save(store);
    }
}
