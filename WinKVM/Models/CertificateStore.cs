using System.Security.Cryptography;
using Windows.Storage;

namespace WinKVM.Protocol;

/// Discriminated union for certificate check results.
public abstract record CertCheckResult
{
    public sealed record Trusted : CertCheckResult;
    public sealed record FirstUse(string Fingerprint) : CertCheckResult;
    public sealed record Changed(string OldFingerprint, string NewFingerprint) : CertCheckResult;
}

/// Trust-on-First-Use certificate store backed by Windows ApplicationData.
public static class CertificateStore
{
    private const string Prefix = "cert_";

    /// Compute a SHA-256 fingerprint as colon-separated hex (matches Swift format).
    public static string Fingerprint(byte[] derData)
    {
        var hash = SHA256.HashData(derData);
        return string.Join(":", hash.Select(b => b.ToString("X2")));
    }

    public static CertCheckResult Check(string host, ushort port, string fingerprint)
    {
        var key      = Prefix + $"{host}:{port}";
        var settings = ApplicationData.Current.LocalSettings;
        if (settings.Values[key] is not string stored)
            return new CertCheckResult.FirstUse(fingerprint);

        return stored == fingerprint
            ? new CertCheckResult.Trusted()
            : new CertCheckResult.Changed(stored, fingerprint);
    }

    public static void Trust(string host, ushort port, string fingerprint)
    {
        var key = Prefix + $"{host}:{port}";
        ApplicationData.Current.LocalSettings.Values[key] = fingerprint;
    }
}
