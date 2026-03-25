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

    public DevicesController(IDeviceService deviceService)
    {
        _deviceService = deviceService;
    }

    /// <summary>
    /// Register a new IoT device
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request)
    {
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
    /// Get device by internal ID
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDevice(Guid id)
    {
        var device = await _deviceService.GetDeviceAsync(id);
        if (device == null)
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
    public async Task<IActionResult> GetDeviceByDeviceId(string deviceId)
    {
        var device = await _deviceService.GetDeviceByDeviceIdAsync(deviceId);
        if (device == null)
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
    public async Task<IActionResult> GetDevicesByTenant(Guid tenantId)
    {
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
    public async Task<IActionResult> GetDevicesByType(string deviceType, [FromQuery] Guid? tenantId = null)
    {
        var devices = await _deviceService.GetDevicesByTypeAsync(deviceType, tenantId);

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
    public async Task<IActionResult> UpdateDevice(Guid id, [FromBody] UpdateDeviceRequest request)
    {
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
    public async Task<IActionResult> DeactivateDevice(Guid id)
    {
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
    public async Task<IActionResult> GetStatistics([FromQuery] Guid? tenantId = null)
    {
        var stats = await _deviceService.GetStatisticsAsync(tenantId);

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
}
