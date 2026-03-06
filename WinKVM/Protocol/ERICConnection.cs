using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinKVM.Protocol;

/// Low-level TLS connection to the KVM. Wraps SslStream over TCP.
/// Async reads use an internal ring-buffer, resolving awaiting callers as data arrives.
public sealed class ERICConnection : IAsyncDisposable
{
    private TcpClient?  _tcp;
    private SslStream?  _ssl;
    private readonly SemaphoreSlim _readLock  = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public long TotalBytesRead    { get; private set; }
    public long TotalBytesWritten { get; private set; }

    // ── Connection ──────────────────────────────────────────────────────────

    /// Connect with TOFU certificate verification.
    /// <param name="onCertChallenge">Called for unknown/changed certs. Return true to trust.</param>
    public async Task ConnectAsync(
        string host, ushort port,
        Func<string, string, Task<bool>>? onCertChallenge = null,
        CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        _tcp.NoDelay = true;
        await _tcp.ConnectAsync(host, port, ct);

        _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false,
            (_, cert, _, _) => ValidateCertificate(cert, host, port, onCertChallenge));

        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                | System.Security.Authentication.SslProtocols.Tls13,
        };
        await _ssl.AuthenticateAsClientAsync(sslOptions, ct);
    }

    private bool ValidateCertificate(
        System.Security.Cryptography.X509Certificates.X509Certificate? cert,
        string host, ushort port,
        Func<string, string, Task<bool>>? onCertChallenge)
    {
        if (cert is null) return true; // no cert — accept (matches Swift fallback)

        var derBytes    = cert.Export(X509ContentType.Cert);
        var fingerprint = CertificateStore.Fingerprint(derBytes);
        var result      = CertificateStore.Check(host, port, fingerprint);

        switch (result)
        {
            case CertCheckResult.Trusted:
                return true;

            case CertCheckResult.FirstUse(var fp):
                if (onCertChallenge is null)
                {
                    CertificateStore.Trust(host, port, fp);
                    return true;
                }
                var msg1 = $"First connection to {host}:{port}.\n\n" +
                           $"Certificate SHA-256 fingerprint:\n{fp}\n\n" +
                           $"Do you want to trust this certificate?";
                bool accepted1 = onCertChallenge(fp, msg1).GetAwaiter().GetResult();
                if (accepted1) CertificateStore.Trust(host, port, fp);
                return accepted1;

            case CertCheckResult.Changed(var oldFp, var newFp):
                if (onCertChallenge is null) return false;
                var msg2 = $"WARNING: Certificate for {host}:{port} has changed!\n\n" +
                           $"Previous fingerprint:\n{oldFp}\n\n" +
                           $"New fingerprint:\n{newFp}\n\n" +
                           $"This could indicate a man-in-the-middle attack. Trust the new certificate?";
                bool accepted2 = onCertChallenge(newFp, msg2).GetAwaiter().GetResult();
                if (accepted2) CertificateStore.Trust(host, port, newFp);
                return accepted2;

            default:
                return false;
        }
    }

    public void Disconnect()
    {
        try { _ssl?.Close(); }    catch { }
        try { _tcp?.Close(); }    catch { }
        _ssl = null;
        _tcp = null;
    }

    // ── Read ────────────────────────────────────────────────────────────────

    /// Read exactly <paramref name="count"/> bytes.
    public async Task<byte[]> ReadAsync(int count, CancellationToken ct = default)
    {
        if (_ssl is null) throw new InvalidOperationException("Not connected");
        var buf     = new byte[count];
        int received = 0;
        await _readLock.WaitAsync(ct);
        try
        {
            while (received < count)
            {
                int n = await _ssl.ReadAsync(buf.AsMemory(received, count - received), ct);
                if (n == 0) throw new IOException("Connection closed by remote host");
                received += n;
            }
        }
        finally { _readLock.Release(); }
        TotalBytesRead += count;
        return buf;
    }

    /// Read a single byte.
    public async Task<byte> ReadByteAsync(CancellationToken ct = default)
    {
        var b = await ReadAsync(1, ct);
        return b[0];
    }

    // ── Write ───────────────────────────────────────────────────────────────

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_ssl is null) throw new InvalidOperationException("Not connected");
        await _writeLock.WaitAsync(ct);
        try   { await _ssl.WriteAsync(data, ct); }
        finally { _writeLock.Release(); }
        TotalBytesWritten += data.Length;
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        _readLock.Dispose();
        _writeLock.Dispose();
        if (_ssl is not null) await _ssl.DisposeAsync();
        _tcp?.Dispose();
    }
}
