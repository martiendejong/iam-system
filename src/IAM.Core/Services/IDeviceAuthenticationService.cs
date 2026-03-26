using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IDeviceAuthenticationService
{
    /// <summary>
    /// Authenticate a device using X.509 certificate (capable devices).
    /// </summary>
    Task<DeviceAuthResult> AuthenticateWithCertificateAsync(string certificatePem, string deviceId);

    /// <summary>
    /// Authenticate a device using HMAC-SHA256 (constrained devices).
    /// Password format: "timestamp:nonce:hmac"
    /// </summary>
    Task<DeviceAuthResult> AuthenticateWithHmacAsync(string deviceId, string hmacPassword);

    /// <summary>
    /// Authorize a device action on a specific resource/topic.
    /// </summary>
    Task<DeviceAuthorizationResult> AuthorizeAsync(string deviceId, string resource, string action);

    /// <summary>
    /// Authorize MQTT publish/subscribe based on topic.
    /// </summary>
    Task<DeviceAuthorizationResult> AuthorizeMqttAsync(string deviceId, string topic, string action);

    /// <summary>
    /// Get device claims (permissions) for policy caching at edge gateway.
    /// </summary>
    Task<DeviceClaimsResult> GetDeviceClaimsAsync(string deviceId);
}

public class DeviceAuthResult
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public int ExpiresIn { get; set; }
    public string? DeviceId { get; set; }
    public List<string> Permissions { get; set; } = new();
    public string? Error { get; set; }
}

public class DeviceAuthorizationResult
{
    public bool Allowed { get; set; }
    public string? Reason { get; set; }
    public string? MatchedPermission { get; set; }
}

public class DeviceClaimsResult
{
    public bool Success { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceType { get; set; }
    public string? ResourcePath { get; set; }
    public List<string> Permissions { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string? Error { get; set; }
}
