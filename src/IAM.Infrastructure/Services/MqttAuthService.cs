using System.Collections.Concurrent;
using System.Text.Json;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// MQTT broker authentication and authorization service.
/// Delegates credential validation to IDeviceAuthenticationService and provides
/// MQTT-specific topic-to-resource mapping, superuser checks, and connection tracking.
/// </summary>
public class MqttAuthService : IMqttAuthService
{
    private readonly IAMDbContext _context;
    private readonly IDeviceAuthenticationService _deviceAuthService;
    private readonly ILogger<MqttAuthService> _logger;

    // In-memory stats counters (thread-safe). Reset on app restart.
    // For production persistence, replace with a distributed cache or DB table.
    private static int _totalConnections;
    private static int _totalAuthAttempts;
    private static int _failedAuthAttempts;
    private static int _aclChecks;
    private static int _aclDenied;
    private static readonly ConcurrentDictionary<string, DateTime> _connectedClients = new();

    public MqttAuthService(
        IAMDbContext context,
        IDeviceAuthenticationService deviceAuthService,
        ILogger<MqttAuthService> logger)
    {
        _context = context;
        _deviceAuthService = deviceAuthService;
        _logger = logger;
    }

    public async Task<MqttAuthResult> AuthenticateClientAsync(
        string clientId, string? username, string? password, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalAuthAttempts);

        if (string.IsNullOrWhiteSpace(clientId))
        {
            Interlocked.Increment(ref _failedAuthAttempts);
            return new MqttAuthResult { Allowed = false, Error = "ClientId is required" };
        }

        // Look up the device by clientId (which maps to Device.DeviceId)
        var device = await _context.Devices
            .AsNoTracking()
            .Include(d => d.Tenant)
            .FirstOrDefaultAsync(d => d.DeviceId == clientId && d.IsActive, ct);

        if (device == null)
        {
            Interlocked.Increment(ref _failedAuthAttempts);
            _logger.LogWarning("MQTT auth failed: device {ClientId} not found or inactive", clientId);
            return new MqttAuthResult { Allowed = false, Error = "Device not found or inactive" };
        }

        // Certificate-based authentication:
        // When username is "certificate" or empty/null, the broker has already validated the
        // client certificate via TLS. We trust the clientId identity from the broker.
        if (string.IsNullOrEmpty(username) || username.Equals("certificate", StringComparison.OrdinalIgnoreCase))
        {
            if (device.AuthenticationMethod == "certificate")
            {
                _logger.LogInformation("MQTT auth success (certificate): {ClientId}", clientId);
                return new MqttAuthResult
                {
                    Allowed = true,
                    DeviceId = device.DeviceId,
                    TenantId = device.TenantId.ToString()
                };
            }

            // Device is not configured for certificate auth but no credentials provided
            Interlocked.Increment(ref _failedAuthAttempts);
            _logger.LogWarning("MQTT auth failed: device {ClientId} requires HMAC but no credentials provided", clientId);
            return new MqttAuthResult { Allowed = false, Error = "Credentials required for HMAC device" };
        }

        // HMAC-based authentication:
        // The MQTT password contains the HMAC payload in format "timestamp:nonce:hmac"
        if (device.AuthenticationMethod == "hmac")
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                Interlocked.Increment(ref _failedAuthAttempts);
                return new MqttAuthResult { Allowed = false, Error = "Password is required for HMAC authentication" };
            }

            var authResult = await _deviceAuthService.AuthenticateWithHmacAsync(clientId, password);
            if (!authResult.Success)
            {
                Interlocked.Increment(ref _failedAuthAttempts);
                _logger.LogWarning("MQTT HMAC auth failed for {ClientId}: {Error}", clientId, authResult.Error);
                return new MqttAuthResult { Allowed = false, Error = authResult.Error };
            }

            _logger.LogInformation("MQTT auth success (HMAC): {ClientId}", clientId);
            return new MqttAuthResult
            {
                Allowed = true,
                DeviceId = device.DeviceId,
                TenantId = device.TenantId.ToString()
            };
        }

        Interlocked.Increment(ref _failedAuthAttempts);
        _logger.LogWarning("MQTT auth failed: unknown auth method {Method} for {ClientId}", device.AuthenticationMethod, clientId);
        return new MqttAuthResult { Allowed = false, Error = $"Unsupported authentication method: {device.AuthenticationMethod}" };
    }

    public async Task<bool> IsSuperuserAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return false;

        var device = await _context.Devices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DeviceId == clientId && d.IsActive, ct);

        if (device == null)
            return false;

        var permissions = DeserializePermissions(device.Permissions);

        // A superuser has a wildcard "*" permission or a tenant-level wildcard
        // matching "tenantslug:*" pattern
        return permissions.Any(p => p == "*" || p.EndsWith(":*"));
    }

    public async Task<MqttAclResult> CheckAclAsync(
        string clientId, string topic, MqttAclAction action, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _aclChecks);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(topic))
        {
            Interlocked.Increment(ref _aclDenied);
            return new MqttAclResult { Allowed = false, Reason = "ClientId and topic are required" };
        }

        // Map MQTT action to IAM action string
        var actionStr = action switch
        {
            MqttAclAction.Publish => "publish",
            MqttAclAction.Subscribe => "subscribe",
            _ => "read"
        };

        // Delegate to existing MQTT authorization in DeviceAuthenticationService
        // which handles topic-to-resource conversion and hierarchical permission matching
        var result = await _deviceAuthService.AuthorizeMqttAsync(clientId, topic, actionStr);

        if (!result.Allowed)
        {
            Interlocked.Increment(ref _aclDenied);
            _logger.LogInformation("MQTT ACL denied: {ClientId} {Action} {Topic} - {Reason}",
                clientId, action, topic, result.Reason);
        }

        return new MqttAclResult
        {
            Allowed = result.Allowed,
            MatchedPermission = result.MatchedPermission,
            Reason = result.Reason
        };
    }

    public Task<MqttBrokerStats> GetStatsAsync(CancellationToken ct = default)
    {
        var stats = new MqttBrokerStats
        {
            ConnectedDevices = _connectedClients.Count,
            TotalConnections = _totalConnections,
            TotalAuthAttempts = _totalAuthAttempts,
            FailedAuthAttempts = _failedAuthAttempts,
            AclChecks = _aclChecks,
            AclDenied = _aclDenied
        };

        return Task.FromResult(stats);
    }

    public async Task RecordConnectionAsync(string clientId, string? ipAddress, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalConnections);
        _connectedClients[clientId] = DateTime.UtcNow;

        var device = await _context.Devices
            .FirstOrDefaultAsync(d => d.DeviceId == clientId, ct);

        if (device != null)
        {
            device.IsOnline = true;
            device.LastSeenAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(ipAddress))
                device.LastIpAddress = ipAddress;

            await _context.SaveChangesAsync(ct);
        }

        _logger.LogInformation("MQTT client connected: {ClientId} from {IpAddress}", clientId, ipAddress ?? "unknown");
    }

    public async Task RecordDisconnectionAsync(string clientId, CancellationToken ct = default)
    {
        _connectedClients.TryRemove(clientId, out _);

        var device = await _context.Devices
            .FirstOrDefaultAsync(d => d.DeviceId == clientId, ct);

        if (device != null)
        {
            device.IsOnline = false;
            device.LastSeenAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
        }

        _logger.LogInformation("MQTT client disconnected: {ClientId}", clientId);
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
