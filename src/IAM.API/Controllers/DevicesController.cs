using System.Security.Claims;
using IAM.API.Authorization;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DevicesController : ControllerBase
{
    private readonly IDeviceService _deviceService;
    private readonly ITenantAccessResolver _access;

    public DevicesController(IDeviceService deviceService, ITenantAccessResolver access)
    {
        _deviceService = deviceService;
        _access = access;
    }

    // Task 4726. Devices carry credentials (an HMAC secret at registration) and permissions that feed the device
    // authorization and telemetry command checks, so every action is checked against the caller's tenants:
    // reading needs membership of the device's tenant, changing needs SuperAdmin or a building-management role
    // (TenantAdmin/BuildingOwner/BuildingManager UserRoles row) in it. Device and service-account tokens get 403.
    // The tenant of an existing device always comes from the stored row, never from the request body.

    private ObjectResult ForbiddenChange() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Only SuperAdmin or a building owner/manager of the device's tenant can change devices." });

    private ObjectResult ForbiddenRead() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "You do not have access to the devices of this tenant." });

    private Guid? CallerUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;

    /// <summary>
    /// Register a new IoT device
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request, CancellationToken ct = default)
    {
        // Authorize before anything else: the duplicate-id check in the service would otherwise tell an
        // unauthorized caller which device ids exist in other tenants.
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManage(request.TenantId))
            return ForbiddenChange();

        // The audit field comes from the caller, never from the body.
        request.ProvisionedByUserId = CallerUserId();

        var result = await _deviceService.RegisterDeviceAsync(request);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        var response = new
        {
            id = result.Device!.Id,
            deviceId = result.Device.DeviceId,
            name = result.Device.Name,
            deviceType = result.Device.DeviceType,
            authenticationMethod = result.Device.AuthenticationMethod,
            tenantId = result.Device.TenantId,
            resourcePath = result.Device.ResourcePath,
            isActive = result.Device.IsActive,
            createdAt = result.Device.CreatedAt,
            sharedSecret = result.SharedSecret // Only present for HMAC devices, returned once
        };

        return CreatedAtAction(nameof(GetDevice), new { id = result.Device.Id }, response);
    }

    /// <summary>
    /// List all devices
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetDevices(CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
            return ForbiddenRead();

        var devices = await _deviceService.GetAllDevicesAsync(access.ReadableTenants);

        return Ok(devices.Select(d => new
        {
            id = d.Id,
            deviceId = d.DeviceId,
            name = d.Name,
            deviceType = d.DeviceType,
            authenticationMethod = d.AuthenticationMethod,
            isActive = d.IsActive,
            isOnline = d.IsOnline,
            lastSeenAt = d.LastSeenAt,
            resourcePath = d.ResourcePath
        }));
    }

    /// <summary>
    /// Get device by internal ID
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDevice(Guid id, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
            return ForbiddenRead();

        // A device of a tenant the caller cannot read is reported exactly like an unknown one.
        var device = await _deviceService.GetDeviceAsync(id);
        if (device == null || !access.CanRead(device.TenantId))
        {
            return NotFound(new { error = "Device not found" });
        }

        return Ok(new
        {
            id = device.Id,
            deviceId = device.DeviceId,
            name = device.Name,
            deviceType = device.DeviceType,
            authenticationMethod = device.AuthenticationMethod,
            tenantId = device.TenantId,
            tenantName = device.Tenant?.Name,
            resourcePath = device.ResourcePath,
            isActive = device.IsActive,
            isOnline = device.IsOnline,
            lastSeenAt = device.LastSeenAt,
            lastAuthenticatedAt = device.LastAuthenticatedAt,
            isProvisioned = device.IsProvisioned,
            provisionedAt = device.ProvisionedAt,
            metadata = device.Metadata,
            tags = device.Tags,
            certificates = device.Certificates.Select(c => new
            {
                id = c.Id,
                serialNumber = c.SerialNumber,
                thumbprint = c.Thumbprint,
                subjectName = c.SubjectName,
                issuerName = c.IssuerName,
                notBefore = c.NotBefore,
                notAfter = c.NotAfter,
                status = c.Status,
                createdAt = c.CreatedAt
            }),
            createdAt = device.CreatedAt,
            updatedAt = device.UpdatedAt
        });
    }

    /// <summary>
    /// Get device by human-readable device ID
    /// </summary>
    [HttpGet("by-device-id/{deviceId}")]
    public async Task<IActionResult> GetDeviceByDeviceId(string deviceId, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
            return ForbiddenRead();

        var device = await _deviceService.GetDeviceByDeviceIdAsync(deviceId);
        if (device == null || !access.CanRead(device.TenantId))
        {
            return NotFound(new { error = "Device not found" });
        }

        return Ok(new
        {
            id = device.Id,
            deviceId = device.DeviceId,
            name = device.Name,
            deviceType = device.DeviceType,
            authenticationMethod = device.AuthenticationMethod,
            tenantId = device.TenantId,
            tenantName = device.Tenant?.Name,
            resourcePath = device.ResourcePath,
            isActive = device.IsActive,
            isOnline = device.IsOnline,
            lastSeenAt = device.LastSeenAt,
            metadata = device.Metadata,
            tags = device.Tags,
            createdAt = device.CreatedAt
        });
    }

    /// <summary>
    /// List devices by tenant
    /// </summary>
    [HttpGet("by-tenant/{tenantId:guid}")]
    public async Task<IActionResult> GetDevicesByTenant(Guid tenantId, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanRead(tenantId))
            return ForbiddenRead();

        var devices = await _deviceService.GetDevicesByTenantAsync(tenantId);

        return Ok(devices.Select(d => new
        {
            id = d.Id,
            deviceId = d.DeviceId,
            name = d.Name,
            deviceType = d.DeviceType,
            authenticationMethod = d.AuthenticationMethod,
            isActive = d.IsActive,
            isOnline = d.IsOnline,
            lastSeenAt = d.LastSeenAt,
            resourcePath = d.ResourcePath
        }));
    }

    /// <summary>
    /// List devices by type (optionally filtered by tenant)
    /// </summary>
    [HttpGet("by-type/{deviceType}")]
    public async Task<IActionResult> GetDevicesByType(string deviceType, [FromQuery] Guid? tenantId = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused || (tenantId.HasValue && !access.CanRead(tenantId.Value)))
            return ForbiddenRead();

        // Without a tenant the list covers only the caller's tenants (null = every tenant, SuperAdmin only).
        var devices = await _deviceService.GetDevicesByTypeAsync(deviceType, tenantId, access.ReadableTenants);

        return Ok(devices.Select(d => new
        {
            id = d.Id,
            deviceId = d.DeviceId,
            name = d.Name,
            deviceType = d.DeviceType,
            isActive = d.IsActive,
            isOnline = d.IsOnline,
            lastSeenAt = d.LastSeenAt,
            resourcePath = d.ResourcePath,
            tenantId = d.TenantId,
            tenantName = d.Tenant?.Name
        }));
    }

    /// <summary>
    /// Update a device
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateDevice(Guid id, [FromBody] UpdateDeviceRequest request, CancellationToken ct = default)
    {
        // Covers IsActive too: a PUT can re-activate a deactivated device, so it needs the same gate as deactivate.
        var denied = await CheckMayChangeAsync(id, ct);
        if (denied != null)
            return denied;

        var result = await _deviceService.UpdateDeviceAsync(id, request);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(new
        {
            id = result.Device!.Id,
            deviceId = result.Device.DeviceId,
            name = result.Device.Name,
            deviceType = result.Device.DeviceType,
            isActive = result.Device.IsActive,
            updatedAt = result.Device.UpdatedAt
        });
    }

    /// <summary>
    /// Deactivate a device (revokes all certificates)
    /// </summary>
    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> DeactivateDevice(Guid id, CancellationToken ct = default)
    {
        var denied = await CheckMayChangeAsync(id, ct);
        if (denied != null)
            return denied;

        var success = await _deviceService.DeactivateDeviceAsync(id);
        if (!success)
        {
            return NotFound(new { error = "Device not found" });
        }

        return Ok(new { message = "Device deactivated and all certificates revoked" });
    }

    /// <summary>
    /// Get device statistics (optionally filtered by tenant)
    /// </summary>
    [HttpGet("statistics")]
    public async Task<IActionResult> GetStatistics([FromQuery] Guid? tenantId = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused || (tenantId.HasValue && !access.CanRead(tenantId.Value)))
            return ForbiddenRead();

        var stats = await _deviceService.GetStatisticsAsync(tenantId, access.ReadableTenants);

        return Ok(new
        {
            totalDevices = stats.TotalDevices,
            activeDevices = stats.ActiveDevices,
            onlineDevices = stats.OnlineDevices,
            certificateDevices = stats.CertificateDevices,
            hmacDevices = stats.HmacDevices,
            devicesByType = stats.DevicesByType
        });
    }

    /// <summary>
    /// Gate for update and deactivate. The privilege check runs BEFORE the lookup, so a caller who manages nothing
    /// gets 403 for any id (no existence oracle); then the stored device's tenant must be one the caller manages.
    /// Returns the error result, or null when the change is allowed.
    /// </summary>
    private async Task<ObjectResult?> CheckMayChangeAsync(Guid id, CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManageAny)
            return ForbiddenChange();

        var tenantId = await _deviceService.GetDeviceTenantIdAsync(id);
        if (tenantId == null)
            return NotFound(new { error = "Device not found" });

        return access.CanManage(tenantId.Value) ? null : ForbiddenChange();
    }
}
