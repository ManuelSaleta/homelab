namespace Jarvis.Sharp.Security;

using System.Security.Cryptography;
using System.Text;

public static class HmacSigner
{
    public static string ComputeSignature(string secret, byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }
        ArgumentNullException.ThrowIfNull(payload);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(payload);
        return $"sha256={Convert.ToHexStringLower(hash)}";
    }

    public static bool VerifySignature(string secret, byte[] payload, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader) || payload == null)
        {
            return false;
        }

        var expected = ComputeSignature(secret, payload);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(signatureHeader.Trim());

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
