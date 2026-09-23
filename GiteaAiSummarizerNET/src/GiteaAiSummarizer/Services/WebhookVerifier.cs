using System.Security.Cryptography;
using System.Text;

namespace GiteaAiSummarizer.Services;

public class WebhookVerifier(IConfiguration config)
{
    private readonly byte[] _secret =
        Encoding.UTF8.GetBytes(config["Gitea:WebhookSecret"] ?? string.Empty);

    public bool Verify(string payload, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        if (_secret.Length == 0) return false;

        var hash = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload));
        var expected = Convert.ToHexStringLower(hash);

        // Gitea sends the signature as hex string (no sha256= prefix)
        var sig = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature[7..]
            : signature;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(sig));
    }
}
