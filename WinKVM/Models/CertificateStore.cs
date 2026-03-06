using System.Security.Cryptography;
using Windows.Storage;

namespace WinKVM.Models;

// ── Re-export for Protocol layer ────────────────────────────────────────────
namespace WinKVM.Protocol
{
    public enum CertCheckResult
    {
        Unknown,
        Trusted,
        FirstUse,
        Changed,
    }

    // Discriminated-union-style helpers
    public record CertCheckResultTrusted  : CertCheckResult { public static implicit operator CertCheckResult(CertCheckResultTrusted  _) => CertCheckResult.Trusted; }
    public record CertCheckResultFirstUse(string Fingerprint);
    public record CertCheckResultChanged(string OldFingerprint, string NewFingerprint);
}

namespace WinKVM.Protocol
{
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

        public static object Check(string host, ushort port, string fingerprint)
        {
            var key      = Prefix + $"{host}:{port}";
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values[key] is not string stored)
                return new CertCheckResultFirstUse(fingerprint);

            return stored == fingerprint
                ? (object)CertCheckResult.Trusted
                : new CertCheckResultChanged(stored, fingerprint);
        }

        public static void Trust(string host, ushort port, string fingerprint)
        {
            var key = Prefix + $"{host}:{port}";
            ApplicationData.Current.LocalSettings.Values[key] = fingerprint;
        }
    }
}
