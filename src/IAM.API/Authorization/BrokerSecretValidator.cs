using System.Security.Cryptography;
using System.Text;

namespace IAM.API.Authorization;

/// <summary>Outcome of checking the MQTT broker shared secret on a request.</summary>
public enum BrokerSecretResult
{
    /// <summary>The caller presented the configured secret (or Development with no secret configured).</summary>
    Accepted,
    /// <summary>No Mqtt:BrokerSecret is configured and the host is not Development: fail closed.</summary>
    NotConfigured,
    /// <summary>A secret is configured but the header is missing or does not match.</summary>
    Rejected
}

/// <summary>
/// Task 4718: the broker shared secret (X-Broker-Secret header vs Mqtt:BrokerSecret).
/// Fails closed: with no secret configured the endpoints are open only in Development.
/// The comparison is constant-time so the secret cannot be recovered by timing.
/// </summary>
public static class BrokerSecretValidator
{
    public const string HeaderName = "X-Broker-Secret";
    public const string ConfigKey = "Mqtt:BrokerSecret";

    public static BrokerSecretResult Check(IConfiguration configuration, IHostEnvironment environment, HttpRequest request)
    {
        var expected = configuration[ConfigKey];
        if (string.IsNullOrEmpty(expected))
            return environment.IsDevelopment() ? BrokerSecretResult.Accepted : BrokerSecretResult.NotConfigured;

        var provided = request.Headers[HeaderName].FirstOrDefault();
        return FixedTimeEquals(provided, expected) ? BrokerSecretResult.Accepted : BrokerSecretResult.Rejected;
    }

    /// <summary>True only when a secret is configured AND the request carries exactly that secret.</summary>
    public static bool PresentsConfiguredSecret(IConfiguration configuration, HttpRequest request)
    {
        var expected = configuration[ConfigKey];
        if (string.IsNullOrEmpty(expected)) return false;
        return FixedTimeEquals(request.Headers[HeaderName].FirstOrDefault(), expected);
    }

    private static bool FixedTimeEquals(string? provided, string expected)
    {
        if (provided == null) return false;
        // Hash both sides so unequal lengths do not short-circuit (FixedTimeEquals needs equal lengths).
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
