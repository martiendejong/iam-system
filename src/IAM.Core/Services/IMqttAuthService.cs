namespace IAM.Core.Services;

/// <summary>
/// MQTT broker authentication and authorization service.
/// Used by MQTT broker plugins (Mosquitto, EMQX, HiveMQ) to authenticate
/// and authorize device connections and topic access.
/// </summary>
public interface IMqttAuthService
{
    /// <summary>
    /// Authenticate an MQTT client by clientId and optional credentials.
    /// ClientId maps to deviceId in the IAM system.
    /// </summary>
    Task<MqttAuthResult> AuthenticateClientAsync(string clientId, string? username, string? password, CancellationToken ct = default);

    /// <summary>
    /// Check if a client has superuser (wildcard) privileges.
    /// Superusers bypass ACL checks entirely.
    /// </summary>
    Task<bool> IsSuperuserAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Check if a client is allowed to publish/subscribe to a specific topic.
    /// Converts MQTT topic paths to IAM resource paths for hierarchical permission matching.
    /// </summary>
    Task<MqttAclResult> CheckAclAsync(string clientId, string topic, MqttAclAction action, CancellationToken ct = default);

    /// <summary>
    /// Get MQTT broker connection and authentication statistics.
    /// </summary>
    Task<MqttBrokerStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Record a device connection event. Updates LastSeenAt and IsOnline status.
    /// </summary>
    Task RecordConnectionAsync(string clientId, string? ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Record a device disconnection event. Sets IsOnline = false.
    /// </summary>
    Task RecordDisconnectionAsync(string clientId, CancellationToken ct = default);
}

public class MqttAuthResult
{
    public bool Allowed { get; set; }
    public string? Error { get; set; }
    public string? DeviceId { get; set; }
    public string? TenantId { get; set; }
}

public class MqttAclResult
{
    public bool Allowed { get; set; }
    public string? MatchedPermission { get; set; }
    public string? Reason { get; set; }
}

public enum MqttAclAction
{
    Publish = 1,
    Subscribe = 2
}

public class MqttBrokerStats
{
    public int ConnectedDevices { get; set; }
    public int TotalConnections { get; set; }
    public int TotalAuthAttempts { get; set; }
    public int FailedAuthAttempts { get; set; }
    public int AclChecks { get; set; }
    public int AclDenied { get; set; }
}
