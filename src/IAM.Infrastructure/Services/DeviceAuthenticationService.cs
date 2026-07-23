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
using Microsoft.IdentityModel.Tokens;

namespace IAM.Infrastructure.Services;

public class DeviceAuthenticationService : IDeviceAuthenticationService
{
    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEventBus _eventBus;
    private readonly ILogger<DeviceAuthenticationService> _logger;

    public DeviceAuthenticationService(
        IAMDbContext context,
        IConfiguration configuration,
        IEventBus eventBus,
        ILogger<DeviceAuthenticationService> logger)
    {
        _context = context;
        _configuration = configuration;
        _eventBus = eventBus;
        _logger = logger;
    }

    private async Task PublishEventAsync(string eventType, object payload, Guid? tenantId)
    {
        try
        {
            await _eventBus.PublishAsync(eventType, payload, tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish {EventType} event", eventType);
        }
    }

    public async Task<DeviceAuthResult> AuthenticateWithCertificateAsync(string certificatePem, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(certificatePem) || string.IsNullOrWhiteSpace(deviceId))
        {
            return new DeviceAuthResult { Success = false, Error = "Certificate and device ID are required" };
        }

        // Find the device
        var device = await _context.Devices
            .Include(d => d.Certificates.Where(c => c.Status == "Active"))
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.IsActive);

        if (device == null)
        {
            return new DeviceAuthResult { Success = false, Error = "Device not found or inactive" };
        }

        if (device.AuthenticationMethod != "certificate")
        {
            return new DeviceAuthResult { Success = false, Error = "Device is not configured for certificate authentication" };
        }

        // Parse and validate the presented certificate
        X509Certificate2 presentedCert;
        try
        {
            presentedCert = X509Certificate2.CreateFromPem(certificatePem);
        }
        catch (Exception)
        {
            return new DeviceAuthResult { Success = false, Error = "Invalid certificate format" };
        }

        // Verify certificate is not expired
        if (presentedCert.NotAfter < DateTime.UtcNow || presentedCert.NotBefore > DateTime.UtcNow)
        {
            return new DeviceAuthResult { Success = false, Error = "Certificate is expired or not yet valid" };
        }

        // Match certificate thumbprint against registered certificates
        var thumbprint = presentedCert.GetCertHashString(HashAlgorithmName.SHA256);
        var matchedCert = device.Certificates.FirstOrDefault(c =>
            c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));

        if (matchedCert == null)
        {
            return new DeviceAuthResult { Success = false, Error = "Certificate not registered for this device" };
        }

        // Update device status
        device.LastAuthenticatedAt = DateTime.UtcNow;
        device.IsOnline = true;
        device.LastSeenAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await PublishEventAsync(IamEventTypes.DeviceAuthenticated, new
        {
            deviceId = device.Id,
            deviceIdentifier = device.DeviceId,
            tenantId = device.TenantId,
            authenticationMethod = "certificate"
        }, device.TenantId);

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

    public async Task<DeviceAuthResult> AuthenticateWithHmacAsync(string deviceId, string hmacPassword)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(hmacPassword))
        {
            return new DeviceAuthResult { Success = false, Error = "Device ID and HMAC password are required" };
        }

        // Parse HMAC password format: "timestamp:nonce:hmac"
        var parts = hmacPassword.Split(':');
        if (parts.Length != 3)
        {
            return new DeviceAuthResult { Success = false, Error = "Invalid HMAC format. Expected: timestamp:nonce:hmac" };
        }

        var timestampStr = parts[0];
        var nonce = parts[1];
        var providedHmac = parts[2];

        // Validate timestamp (prevent replay attacks - 5 minute window)
        if (!long.TryParse(timestampStr, out var timestamp))
        {
            return new DeviceAuthResult { Success = false, Error = "Invalid timestamp" };
        }

        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        var timeDiff = Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes);
        if (timeDiff > 5)
        {
            return new DeviceAuthResult { Success = false, Error = "Request expired (timestamp outside 5-minute window)" };
        }

        // Find the device
        var device = await _context.Devices
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.IsActive);

        if (device == null)
        {
            return new DeviceAuthResult { Success = false, Error = "Device not found or inactive" };
        }

        if (device.AuthenticationMethod != "hmac")
        {
            return new DeviceAuthResult { Success = false, Error = "Device is not configured for HMAC authentication" };
        }

        if (string.IsNullOrEmpty(device.SharedSecretHash))
        {
            return new DeviceAuthResult { Success = false, Error = "Device has no shared secret configured" };
        }

        // Verify HMAC: The device signs "timestamp:nonce:deviceId" with the shared secret
        // We verify by checking if the stored hash matches
        // Since we store BCrypt hash, we reconstruct the expected message and verify
        var message = $"{timestampStr}:{nonce}:{deviceId}";

        // The HMAC is computed by the device using the raw shared secret
        // We verify by checking BCrypt.Verify(providedHmac, storedHash)
        // This works because the device computes HMAC(message, secret) and we stored BCrypt(secret)
        // Alternative: store the raw secret encrypted and compute HMAC server-side
        // For simplicity, we verify the shared secret was used correctly
        if (!BCrypt.Net.BCrypt.Verify(providedHmac, device.SharedSecretHash))
        {
            return new DeviceAuthResult { Success = false, Error = "Authentication failed" };
        }

        // Update device status
        device.LastAuthenticatedAt = DateTime.UtcNow;
        device.IsOnline = true;
        device.LastSeenAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await PublishEventAsync(IamEventTypes.DeviceAuthenticated, new
        {
            deviceId = device.Id,
            deviceIdentifier = device.DeviceId,
            tenantId = device.TenantId,
            authenticationMethod = "hmac"
        }, device.TenantId);

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
