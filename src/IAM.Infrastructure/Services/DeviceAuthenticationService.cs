using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Infrastructure.Services;

public class DeviceAuthenticationService : IDeviceAuthenticationService
{
    /// <summary>
    /// Task 5159: the one answer every failed login gets, whatever was wrong (unknown or inactive device, wrong secret,
    /// locked device, replayed nonce, stale timestamp, untrusted caller, unknown certificate). The real reason goes to
    /// the server log only.
    /// </summary>
    public const string GenericFailure = "Authentication failed";

    private const int MaxNonceLength = 128;

    // Hash used to spend the same BCrypt time on a login that already failed for another reason, so the response time
    // does not tell an unknown or locked device from a known one with a wrong secret (same cost factor as DeviceService).
    private static readonly Lazy<string> DummySecretHash = new(() =>
        BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), workFactor: 12));

    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IDeviceLoginGuard _loginGuard;
    private readonly IDeviceGatewayVerifier _gatewayVerifier;
    private readonly ILogger<DeviceAuthenticationService> _logger;

    public DeviceAuthenticationService(
        IAMDbContext context,
        IConfiguration configuration,
        IDeviceLoginGuard loginGuard,
        IDeviceGatewayVerifier gatewayVerifier,
        ILogger<DeviceAuthenticationService>? logger = null)
    {
        _context = context;
        _configuration = configuration;
        _loginGuard = loginGuard;
        _gatewayVerifier = gatewayVerifier;
        _logger = logger ?? NullLogger<DeviceAuthenticationService>.Instance;
    }

    public async Task<DeviceAuthResult> AuthenticateWithCertificateAsync(string certificatePem, string deviceId, string? gatewayKey)
    {
        // The certificate PEM is public, so on its own it proves nothing: only a trusted edge gateway (which has
        // verified the device private key in the mTLS handshake) may present it. Checked before anything is looked up.
        if (!_gatewayVerifier.IsTrusted(gatewayKey))
        {
            return Fail("certificate login from a caller that is not a trusted gateway", deviceId);
        }

        if (string.IsNullOrWhiteSpace(certificatePem) || string.IsNullOrWhiteSpace(deviceId))
        {
            return Fail("certificate or device id missing", deviceId);
        }

        var device = await _context.Devices
            .Include(d => d.Certificates.Where(c => c.Status == "Active"))
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.IsActive);

        if (device == null)
        {
            return Fail("device not found or inactive", deviceId);
        }

        if (_loginGuard.IsLockedOut(deviceId))
        {
            return Fail("device is locked after repeated failures", deviceId);
        }

        if (device.AuthenticationMethod != "certificate")
        {
            return FailKnownDevice("device is not configured for certificate authentication", deviceId);
        }

        // Parse and validate the presented certificate
        X509Certificate2 presentedCert;
        try
        {
            presentedCert = X509Certificate2.CreateFromPem(certificatePem);
        }
        catch (Exception)
        {
            return FailKnownDevice("invalid certificate format", deviceId);
        }

        using (presentedCert)
        {
            // Verify certificate is not expired. NotBefore/NotAfter are local times; compare them as UTC.
            var nowUtc = DateTime.UtcNow;
            if (presentedCert.NotAfter.ToUniversalTime() < nowUtc || presentedCert.NotBefore.ToUniversalTime() > nowUtc)
            {
                return FailKnownDevice("certificate is expired or not yet valid", deviceId);
            }

            // Match the certificate thumbprint (SHA-256, like every stored DeviceCertificate thumbprint) against the registered certificates
            var thumbprint = CertificateThumbprint.Sha256(presentedCert);
            var matchedCert = device.Certificates.FirstOrDefault(c =>
                c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));

            if (matchedCert == null)
            {
                return FailKnownDevice("certificate not registered for this device", deviceId);
            }
        }

        _loginGuard.RecordSuccess(deviceId);
        return await CompleteLoginAsync(device);
    }

    public async Task<DeviceAuthResult> AuthenticateWithHmacAsync(string deviceId, string hmacPassword)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(hmacPassword))
        {
            return Fail("device id or password missing", deviceId);
        }

        // Every check that can fail runs before the BCrypt step, and the BCrypt step runs exactly once whether or not an
        // earlier check failed, so the answer and the response time are the same for every kind of failure.
        string? failure = null;
        var nonce = string.Empty;
        var providedHmac = string.Empty;

        // Parse HMAC password format: "timestamp:nonce:hmac"
        var parts = hmacPassword.Split(':');
        if (parts.Length != 3 || parts[1].Length == 0 || parts[1].Length > MaxNonceLength)
        {
            failure = "invalid password format, expected timestamp:nonce:hmac";
        }
        else
        {
            nonce = parts[1];
            providedHmac = parts[2];

            // Validate timestamp (5 minute window; the nonce store below turns that window into a one-time use)
            if (!long.TryParse(parts[0], out var timestamp))
            {
                failure = "invalid timestamp";
            }
            else
            {
                var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
                if (Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes) > DeviceLoginGuard.TimestampWindow.TotalMinutes)
                {
                    failure = "timestamp outside the 5-minute window";
                }
            }
        }

        var device = await _context.Devices
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.IsActive);

        // Only a device that exists and is not locked can collect a failure; anything else is refused without counting.
        var countsAsFailure = false;
        if (device == null)
        {
            failure ??= "device not found or inactive";
        }
        else if (_loginGuard.IsLockedOut(deviceId))
        {
            failure ??= "device is locked after repeated failures";
        }
        else
        {
            countsAsFailure = true;
            if (device.AuthenticationMethod != "hmac")
            {
                failure ??= "device is not configured for HMAC authentication";
            }
            else if (string.IsNullOrEmpty(device.SharedSecretHash))
            {
                failure ??= "device has no shared secret configured";
            }
        }

        // The device presents its secret in the HMAC position; a real HMAC check needs a recoverable secret and is the
        // phase 2 follow-up. Until then the stored BCrypt hash is verified against what was sent.
        var secretHash = failure is null ? device!.SharedSecretHash! : DummySecretHash.Value;
        var secretMatches = BCrypt.Net.BCrypt.Verify(providedHmac, secretHash);

        if (failure is null && !secretMatches)
        {
            failure = "wrong secret";
        }

        // Only a request that proved the secret may spend its nonce, so an outsider cannot use up a device's nonces.
        if (failure is null && !_loginGuard.TryUseNonce(deviceId, nonce))
        {
            failure = "nonce already used (replay)";
        }

        if (failure is not null)
        {
            return countsAsFailure ? FailKnownDevice(failure, deviceId) : Fail(failure, deviceId);
        }

        _loginGuard.RecordSuccess(deviceId);
        return await CompleteLoginAsync(device!);
    }

    private DeviceAuthResult Fail(string reason, string? deviceId)
    {
        _logger.LogWarning("Device login refused for {DeviceId}: {Reason}", Truncate(deviceId), reason);
        return new DeviceAuthResult { Success = false, Error = GenericFailure };
    }

    /// <summary>A failure of a device that exists and is not locked: counts towards that device's lock.</summary>
    private DeviceAuthResult FailKnownDevice(string reason, string deviceId)
    {
        _loginGuard.RecordFailure(deviceId);
        return Fail(reason, deviceId);
    }

    // The id comes from an unauthenticated request: cut it short and drop control characters before it reaches the log.
    private static string Truncate(string? value) =>
        value is null ? string.Empty : new string(value.Take(64).Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private async Task<DeviceAuthResult> CompleteLoginAsync(Device device)
    {
        // Update device status
        device.LastAuthenticatedAt = DateTime.UtcNow;
        device.IsOnline = true;
        device.LastSeenAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Generate device access token
        var permissions = DeserializePermissions(device.Permissions);
        var accessToken = GenerateDeviceAccessToken(device, permissions);

        return new DeviceAuthResult
        {
            Success = true,
            AccessToken = accessToken,
            ExpiresIn = GetTokenExpirationSeconds(),
            DeviceId = device.DeviceId,
            Permissions = permissions
        };
    }

    public async Task<DeviceAuthorizationResult> AuthorizeAsync(string deviceId, string resource, string action)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(resource))
        {
            return new DeviceAuthorizationResult { Allowed = false, Reason = "Device ID and resource are required" };
        }

        var device = await _context.Devices
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.IsActive);

        if (device == null)
        {
            return new DeviceAuthorizationResult { Allowed = false, Reason = "Device not found or inactive" };
        }

        var permissions = DeserializePermissions(device.Permissions);
        var requestedPermission = string.IsNullOrWhiteSpace(action)
            ? resource
            : $"{resource}:{action}";

        // Check hierarchical permission match
        foreach (var permission in permissions)
        {
            if (IsHierarchicalMatch(permission, requestedPermission))
            {
                return new DeviceAuthorizationResult
                {
                    Allowed = true,
                    MatchedPermission = permission
                };
            }
        }

        return new DeviceAuthorizationResult
        {
            Allowed = false,
            Reason = $"No permission found for {requestedPermission}"
        };
    }

    public async Task<DeviceAuthorizationResult> AuthorizeMqttAsync(string deviceId, string topic, string action)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return new DeviceAuthorizationResult { Allowed = false, Reason = "Topic is required" };
        }

        // Convert MQTT topic to resource path
        // MQTT: "acme/headquarters/floor-3/hvac/unit-247/telemetry"
        // Resource: "acme:headquarters:floor-3:hvac:unit-247:telemetry"
        var resource = topic.Replace('/', ':');

        // Map MQTT actions to resource actions
        var resourceAction = action?.ToLowerInvariant() switch
        {
            "publish" => "write",
            "subscribe" => "read",
            _ => action ?? "read"
        };

        return await AuthorizeAsync(deviceId, resource, resourceAction);
    }

    public async Task<DeviceClaimsResult> GetDeviceClaimsAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return new DeviceClaimsResult { Success = false, Error = "Device ID is required" };
        }

        var device = await _context.Devices
            .Include(d => d.Tenant)
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.IsActive);

        if (device == null)
        {
            return new DeviceClaimsResult { Success = false, Error = "Device not found or inactive" };
        }

        var permissions = DeserializePermissions(device.Permissions);
        var metadata = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(device.Metadata))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(device.Metadata);
                if (parsed != null) metadata = parsed;
            }
            catch
            {
                // Metadata is not a flat dictionary, store as raw JSON
                metadata["_raw"] = device.Metadata;
            }
        }

        // Add standard claims
        metadata["tenant_id"] = device.TenantId.ToString();
        metadata["tenant_name"] = device.Tenant?.Name ?? "";
        metadata["auth_method"] = device.AuthenticationMethod;
        metadata["is_online"] = device.IsOnline.ToString().ToLowerInvariant();

        return new DeviceClaimsResult
        {
            Success = true,
            DeviceId = device.DeviceId,
            DeviceType = device.DeviceType,
            ResourcePath = device.ResourcePath,
            Permissions = permissions,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Hierarchical permission matching with wildcard support.
    /// "acme:hq:floor-3:sensor:*" matches "acme:hq:floor-3:sensor:temp-089"
    /// "acme:hq:*" matches "acme:hq:floor-3:hvac:unit-247:telemetry:write"
    /// </summary>
    private static bool IsHierarchicalMatch(string permission, string requested)
    {
        // Exact match
        if (permission == requested) return true;

        // Wildcard at end: "acme:hq:*" matches everything under "acme:hq:"
        if (permission.EndsWith(":*"))
        {
            var prefix = permission[..^2]; // Remove ":*"
            return requested.StartsWith(prefix + ":", StringComparison.Ordinal)
                || requested == prefix;
        }

        // Superpath match: "acme:hq:floor-3" implicitly allows "acme:hq:floor-3:sensor:temp:read"
        if (requested.StartsWith(permission + ":", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private string GenerateDeviceAccessToken(Device device, List<string> permissions)
    {
        var claims = new List<Claim>
        {
            new Claim("sub", device.Id.ToString()),
            new Claim("device_id", device.DeviceId),
            new Claim("device_type", device.DeviceType),
            new Claim("tenant_id", device.TenantId.ToString()),
            new Claim("resource_path", device.ResourcePath),
            new Claim("auth_method", device.AuthenticationMethod),
            new Claim("token_type", "device")
        };

        // Add permissions as individual claims
        foreach (var permission in permissions)
        {
            claims.Add(new Claim("permission", permission));
        }

        var secretKey = _configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("JWT secret key not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expirationMinutes = int.Parse(
            _configuration["Jwt:DeviceTokenExpirationMinutes"] ?? "60");

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expirationMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private int GetTokenExpirationSeconds()
    {
        var minutes = int.Parse(
            _configuration["Jwt:DeviceTokenExpirationMinutes"] ?? "60");
        return minutes * 60;
    }

    private static List<string> DeserializePermissions(string permissionsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(permissionsJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
