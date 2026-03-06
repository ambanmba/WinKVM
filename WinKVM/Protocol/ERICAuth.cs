using System.Security.Cryptography;
using System.Text;

namespace WinKVM.Protocol;

/// Handles MD5 challenge-response authentication for the e-RIC protocol.
public static class ERICAuth
{
    /// Compute MD5(challenge + password) and return as uppercase hex string.
    public static string Md5ChallengeResponse(byte[] challenge, string password)
    {
        using var md5 = MD5.Create();
        md5.TransformBlock(challenge, 0, challenge.Length, null, 0);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        md5.TransformFinalBlock(passwordBytes, 0, passwordBytes.Length);
        var digest = md5.Hash!;
        return Convert.ToHexString(digest); // uppercase, matches Swift behaviour
    }
}
