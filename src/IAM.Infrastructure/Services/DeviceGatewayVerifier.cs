using System.Security.Cryptography;
using System.Text;
using IAM.Core.Services;
using Microsoft.Extensions.Configuration;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 5159: a certificate login is only accepted from a trusted edge gateway, which proves itself with a shared key
/// sent in the <c>X-Device-Gateway-Key</c> header. The keys come from <c>DeviceAuth:GatewayKeys</c> (several at once so
/// a key can be rotated) or the single <c>DeviceAuth:GatewayKey</c>. Nothing configured means no certificate login at
/// all: the check fails closed. Keys shorter than <see cref="MinimumKeyLength"/> characters are ignored, so a weak key
/// cannot silently open the door. Keys are read on every call, so a changed value applies without a restart.
/// </summary>
public sealed class DeviceGatewayVerifier : IDeviceGatewayVerifier
{
    public const string HeaderName = "X-Device-Gateway-Key";
    internal const int MinimumKeyLength = 32;

    private readonly IConfiguration _configuration;

    public DeviceGatewayVerifier(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool IsTrusted(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        var configured = (_configuration.GetSection("DeviceAuth:GatewayKeys").Get<string[]>() ?? Array.Empty<string>())
            .Append(_configuration["DeviceAuth:GatewayKey"])
            .Where(k => !string.IsNullOrEmpty(k) && k.Length >= MinimumKeyLength)
            .ToList();

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));

        // Compare against every key without stopping at the first match, on fixed-length hashes.
        var trusted = false;
        foreach (var key in configured)
        {
            var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key!));
            trusted |= CryptographicOperations.FixedTimeEquals(presentedHash, keyHash);
        }

        return trusted;
    }
}
