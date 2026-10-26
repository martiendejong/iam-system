using System.Security.Claims;
using System.Text.Json;
using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IAM.API.Hubs;

/// <summary>
/// Real-time telemetry hub for IoT device data streaming.
/// Devices publish telemetry via MQTT → Edge Gateway → This Hub → Dashboard clients.
/// Dashboard clients subscribe to specific device/tenant streams.
/// Every method checks the caller (task 4708): a device acts only for itself, users read the tenants
/// they belong to, and publishing, status reports and commands need a building-management role in the
/// device's tenant. Tenant and type of the data always come from the device registry.
/// </summary>
[Authorize]
public class TelemetryHub : Hub
{
    private readonly IDeviceAuthenticationService _deviceAuthService;
    private readonly IDeviceService _deviceService;
    private readonly ITelemetryStorageService _telemetryService;
    private readonly ITelemetryAccessAuthorizer _authorizer;
    private readonly ILogger<TelemetryHub> _logger;

    public TelemetryHub(
        IDeviceAuthenticationService deviceAuthService,
        IDeviceService deviceService,
        ITelemetryStorageService telemetryService,
        ITelemetryAccessAuthorizer authorizer,
        ILogger<TelemetryHub> logger)
    {
        _deviceAuthService = deviceAuthService;
        _deviceService = deviceService;
        _telemetryService = telemetryService;
        _authorizer = authorizer;
        _logger = logger;
    }

    private async Task<TelemetryDevice> RequireDeviceAsync(string? deviceId, TelemetryAction action)
    {
        var access = await _authorizer.AuthorizeDeviceAsync(
            Context.User ?? new ClaimsPrincipal(), deviceId, action, Context.ConnectionAborted);
        if (!access.Allowed)
            throw new HubException($"Unauthorized: {access.Message}");

        return access.Device!;
    }

    private async Task<Guid> RequireTenantReadAsync(string? tenantId)
    {
        if (!Guid.TryParse(tenantId, out var parsed))
            throw new HubException("Unauthorized: You do not have access to this tenant.");

        var access = await _authorizer.AuthorizeTenantReadAsync(
            Context.User ?? new ClaimsPrincipal(), parsed, Context.ConnectionAborted);
        if (!access.Allowed)
            throw new HubException($"Unauthorized: {access.Message}");

        return parsed;
    }

    /// <summary>
    /// Subscribe to telemetry from a specific device.
    /// Group name: "device:{deviceId}"
    /// </summary>
    public async Task SubscribeToDevice(string deviceId)
    {
        var device = await RequireDeviceAsync(deviceId, TelemetryAction.Read);
        await Groups.AddToGroupAsync(Context.ConnectionId, TelemetryGroups.Device(device.DeviceId));
    }

    /// <summary>
    /// Unsubscribe from a specific device's telemetry.
    /// </summary>
    public async Task UnsubscribeFromDevice(string deviceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TelemetryGroups.Device(deviceId));
    }

    /// <summary>
    /// Subscribe to all telemetry from devices in a tenant.
    /// Group name: "tenant:{tenantId}"
    /// </summary>
    public async Task SubscribeToTenant(string tenantId)
    {
        var tenant = await RequireTenantReadAsync(tenantId);
        await Groups.AddToGroupAsync(Context.ConnectionId, TelemetryGroups.Tenant(tenant));
    }

    /// <summary>
    /// Unsubscribe from a tenant's telemetry.
    /// </summary>
    public async Task UnsubscribeFromTenant(string tenantId)
    {
        if (Guid.TryParse(tenantId, out var tenant))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, TelemetryGroups.Tenant(tenant));
    }

    /// <summary>
    /// Subscribe to telemetry from all devices of a specific type in the caller's own tenant (the one
    /// tenant they belong to; a caller of several tenants, or a SuperAdmin, uses
    /// <see cref="SubscribeToTenantDeviceType"/>).
    /// Group name: "type:{tenantId}:{deviceType}"
    /// </summary>
    public async Task SubscribeToDeviceType(string deviceType)
    {
        var scope = await _authorizer.ResolveReadScopeAsync(
            Context.User ?? new ClaimsPrincipal(), null, Context.ConnectionAborted);
        if (scope.Decision == TelemetryScopeDecision.Forbidden)
            throw new HubException($"Unauthorized: {scope.Message}");
        if (scope.TenantId == null)
            throw new HubException("Specify a tenant: use SubscribeToTenantDeviceType(tenantId, deviceType).");

        await Groups.AddToGroupAsync(Context.ConnectionId, TelemetryGroups.DeviceType(scope.TenantId.Value, deviceType));
    }

    /// <summary>
    /// Subscribe to telemetry from all devices of a specific type in one tenant.
    /// Group name: "type:{tenantId}:{deviceType}"
    /// </summary>
    public async Task SubscribeToTenantDeviceType(string tenantId, string deviceType)
    {
        var tenant = await RequireTenantReadAsync(tenantId);
        await Groups.AddToGroupAsync(Context.ConnectionId, TelemetryGroups.DeviceType(tenant, deviceType));
    }

    /// <summary>
    /// Called by edge gateways to push telemetry data from devices.
    /// Broadcasts to all subscribed groups (device, tenant, type).
    /// </summary>
    public async Task PublishTelemetry(TelemetryMessage message)
    {
        // The tenant and type of the data come from the device registry, never from the message.
        var device = await RequireDeviceAsync(message.DeviceId, TelemetryAction.Write);

        var receivedAt = DateTime.UtcNow;
        var enrichedMessage = new
        {
            device.DeviceId,
            device.DeviceType,
            TenantId = device.TenantId.ToString(),
            message.DataType,
            message.Payload,
            message.Timestamp,
            receivedAt
        };

        // Persist first, so a failed store broadcasts nothing; the data survives past the broadcast
        // (dashboard history, retention, exports).
        await _telemetryService.IngestAsync(ToTelemetryRecord(message, device));

        await Task.WhenAll(
            Clients.Group(TelemetryGroups.Device(device.DeviceId)).SendAsync("TelemetryReceived", enrichedMessage),
            Clients.Group(TelemetryGroups.Tenant(device.TenantId)).SendAsync("TelemetryReceived", enrichedMessage),
            Clients.Group(TelemetryGroups.DeviceType(device.TenantId, device.DeviceType)).SendAsync("TelemetryReceived", enrichedMessage));
    }

    private static TelemetryRecord ToTelemetryRecord(TelemetryMessage message, TelemetryDevice device)
    {
        var record = new TelemetryRecord
        {
            Id = Guid.NewGuid(),
            DeviceId = device.DeviceId,
            DeviceType = device.DeviceType,
            MetricName = message.DataType,
            TenantId = device.TenantId,
            Timestamp = message.Timestamp
        };

        switch (message.Payload)
        {
            case null:
                break;
            case JsonElement { ValueKind: JsonValueKind.Number } number:
                record.NumericValue = number.GetDouble();
                break;
            case JsonElement { ValueKind: JsonValueKind.String } str:
                record.StringValue = str.GetString();
                break;
            case JsonElement element:
                record.JsonValue = element.GetRawText();
                break;
            case double or int or long or float or decimal:
                record.NumericValue = Convert.ToDouble(message.Payload);
                break;
            case string s:
                record.StringValue = s;
                break;
            default:
                record.JsonValue = JsonSerializer.Serialize(message.Payload);
                break;
        }

        return record;
    }

    /// <summary>
    /// Called by edge gateways to report device status changes.
    /// </summary>
    public async Task ReportDeviceStatus(DeviceStatusMessage message)
    {
        var device = await RequireDeviceAsync(message.DeviceId, TelemetryAction.Write);

        await _deviceService.UpdateDeviceStatusAsync(device.DeviceId, message.IsOnline, message.IpAddress);

        var statusMessage = new
        {
            device.DeviceId,
            message.IsOnline,
            message.IpAddress,
            timestamp = DateTime.UtcNow
        };

        await Clients.Group(TelemetryGroups.Device(device.DeviceId)).SendAsync("DeviceStatusChanged", statusMessage);
        await Clients.Group(TelemetryGroups.Tenant(device.TenantId)).SendAsync("DeviceStatusChanged", statusMessage);
    }

    /// <summary>
    /// Send a command to a device (via edge gateway).
    /// </summary>
    public async Task SendDeviceCommand(DeviceCommandMessage command)
    {
        // The caller must be a user with a building-management role in the device's tenant ...
        var device = await RequireDeviceAsync(command.DeviceId, TelemetryAction.Command);

        // ... on top of the device's own permission check.
        var authResult = await _deviceAuthService.AuthorizeAsync(
            device.DeviceId, command.Resource, "command");

        if (!authResult.Allowed)
        {
            throw new HubException($"Unauthorized: {authResult.Reason}");
        }

        // Forward command to the device's edge gateway
        await Clients.Group(TelemetryGroups.Device(device.DeviceId)).SendAsync("DeviceCommand", new
        {
            device.DeviceId,
            command.CommandType,
            command.Payload,
            issuedBy = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            issuedAt = DateTime.UtcNow
        });
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Clean up is handled automatically by SignalR group management
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// SignalR group names. Tenant and device-type groups are per tenant, so a subscription can never
/// see another tenant's data; every broadcast uses the registry's tenant and device id.
/// </summary>
public static class TelemetryGroups
{
    public static string Device(string deviceId) => $"device:{deviceId}";
    public static string Tenant(Guid tenantId) => $"tenant:{tenantId}";
    public static string DeviceType(Guid tenantId, string deviceType) => $"type:{tenantId}:{deviceType}";
}

// Hub message DTOs
public class TelemetryMessage
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty; // e.g., "temperature", "humidity", "occupancy"
    public object? Payload { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class DeviceStatusMessage
{
    public string DeviceId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public string? IpAddress { get; set; }
}

public class DeviceCommandMessage
{
    public string DeviceId { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty; // e.g., "setpoint", "reboot", "configure"
    public object? Payload { get; set; }
}
