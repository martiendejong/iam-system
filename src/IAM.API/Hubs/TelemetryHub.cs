using System.Security.Claims;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IAM.API.Hubs;

/// <summary>
/// Real-time telemetry hub for IoT device data streaming.
/// Devices publish telemetry via MQTT → Edge Gateway → This Hub → Dashboard clients.
/// Dashboard clients subscribe to specific device/tenant streams.
/// </summary>
[Authorize]
public class TelemetryHub : Hub
{
    private readonly IDeviceAuthenticationService _deviceAuthService;
    private readonly IDeviceService _deviceService;
    private readonly ITelemetryStorageService _telemetryService;
    private readonly ILogger<TelemetryHub> _logger;

    public TelemetryHub(
        IDeviceAuthenticationService deviceAuthService,
        IDeviceService deviceService,
        ITelemetryStorageService telemetryService,
        ILogger<TelemetryHub> logger)
    {
        _deviceAuthService = deviceAuthService;
        _deviceService = deviceService;
        _telemetryService = telemetryService;
        _logger = logger;
    }

    /// <summary>
    /// Subscribe to telemetry from a specific device.
    /// Group name: "device:{deviceId}"
    /// </summary>
    public async Task SubscribeToDevice(string deviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}");
    }

    /// <summary>
    /// Unsubscribe from a specific device's telemetry.
    /// </summary>
    public async Task UnsubscribeFromDevice(string deviceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"device:{deviceId}");
    }

    /// <summary>
    /// Subscribe to all telemetry from devices in a tenant.
    /// Group name: "tenant:{tenantId}"
    /// </summary>
    public async Task SubscribeToTenant(string tenantId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
    }

    /// <summary>
    /// Unsubscribe from a tenant's telemetry.
    /// </summary>
    public async Task UnsubscribeFromTenant(string tenantId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
    }

    /// <summary>
    /// Subscribe to telemetry from all devices of a specific type.
    /// Group name: "type:{deviceType}"
    /// </summary>
    public async Task SubscribeToDeviceType(string deviceType)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"type:{deviceType}");
    }

    /// <summary>
    /// Called by edge gateways to push telemetry data from devices.
    /// Broadcasts to all subscribed groups (device, tenant, type).
    /// </summary>
    public async Task PublishTelemetry(TelemetryMessage message)
    {
        // Verify the sender has permission to publish for this device
        var tokenType = Context.User?.FindFirst("token_type")?.Value;
        if (tokenType == "device")
        {
            var tokenDeviceId = Context.User?.FindFirst("device_id")?.Value;
            if (tokenDeviceId != message.DeviceId)
            {
                throw new HubException("Unauthorized: cannot publish telemetry for another device");
            }
        }

        var receivedAt = DateTime.UtcNow;
        var enrichedMessage = new
        {
            message.DeviceId,
            message.DeviceType,
            message.TenantId,
            message.DataType,
            message.Payload,
            message.Timestamp,
            receivedAt
        };

        // Broadcast to all relevant groups simultaneously
        var broadcastTasks = new List<Task>
        {
            Clients.Group($"device:{message.DeviceId}").SendAsync("TelemetryReceived", enrichedMessage),
            Clients.Group($"tenant:{message.TenantId}").SendAsync("TelemetryReceived", enrichedMessage),
            Clients.Group($"type:{message.DeviceType}").SendAsync("TelemetryReceived", enrichedMessage)
        };

        // Persist so the data survives past this broadcast (dashboard history, retention, exports).
        // Without this, telemetry published over the hub was visible live but never queryable afterwards.
        Guid.TryParse(message.TenantId, out var tenantId);
        var record = ToTelemetryRecord(message, tenantId);
        broadcastTasks.Add(_telemetryService.IngestAsync(record));

        await Task.WhenAll(broadcastTasks);
    }

    private static TelemetryRecord ToTelemetryRecord(TelemetryMessage message, Guid tenantId)
    {
        var record = new TelemetryRecord
        {
            Id = Guid.NewGuid(),
            DeviceId = message.DeviceId,
            DeviceType = message.DeviceType,
            MetricName = message.DataType,
            TenantId = tenantId == Guid.Empty ? null : tenantId,
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
        await _deviceService.UpdateDeviceStatusAsync(message.DeviceId, message.IsOnline, message.IpAddress);

        var statusMessage = new
        {
            message.DeviceId,
            message.IsOnline,
            message.IpAddress,
            timestamp = DateTime.UtcNow
        };

        await Clients.Group($"device:{message.DeviceId}").SendAsync("DeviceStatusChanged", statusMessage);
        await Clients.Group($"tenant:{message.TenantId}").SendAsync("DeviceStatusChanged", statusMessage);
    }

    /// <summary>
    /// Send a command to a device (via edge gateway).
    /// </summary>
    public async Task SendDeviceCommand(DeviceCommandMessage command)
    {
        // Verify authorization
        var authResult = await _deviceAuthService.AuthorizeAsync(
            command.DeviceId, command.Resource, "command");

        if (!authResult.Allowed)
        {
            throw new HubException($"Unauthorized: {authResult.Reason}");
        }

        // Forward command to the device's edge gateway
        await Clients.Group($"device:{command.DeviceId}").SendAsync("DeviceCommand", new
        {
            command.DeviceId,
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
