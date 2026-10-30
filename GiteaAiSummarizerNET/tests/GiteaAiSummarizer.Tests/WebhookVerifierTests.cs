using System.Security.Cryptography;
using System.Text;
using GiteaAiSummarizer.Services;
using Microsoft.Extensions.Configuration;

namespace GiteaAiSummarizer.Tests;

public class WebhookVerifierTests
{
    private const string Secret = "test-secret";

    private static WebhookVerifier CreateVerifier(string? secret = Secret)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gitea:WebhookSecret"] = secret })
            .Build();
        return new WebhookVerifier(config);
    }

    private static string Sign(string payload, string secret) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))
        );

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var verifier = CreateVerifier();
        var payload = "{\"action\":\"opened\"}";

        Assert.True(verifier.Verify(payload, Sign(payload, Secret)));
    }

    [Fact]
    public void Verify_ValidSignature_WithSha256Prefix_ReturnsTrue()
    {
        var verifier = CreateVerifier();
        var payload = "{\"action\":\"opened\"}";

        Assert.True(verifier.Verify(payload, "sha256=" + Sign(payload, Secret)));
    }

    [Fact]
    public void Verify_WrongSecret_ReturnsFalse()
    {
        var verifier = CreateVerifier();
        var payload = "{\"action\":\"opened\"}";

        Assert.False(verifier.Verify(payload, Sign(payload, "wrong-secret")));
    }

    [Fact]
    public void Verify_MissingSignature_ReturnsFalse()
    {
        Assert.False(CreateVerifier().Verify("payload", null));
    }

    [Fact]
    public void Verify_NoSecretConfigured_ReturnsFalse()
    {
        var verifier = CreateVerifier(secret: null);
        Assert.False(verifier.Verify("payload", Sign("payload", "")));
    }
}
