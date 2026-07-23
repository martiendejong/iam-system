using IAM.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// Device authentication endpoints.
/// These endpoints are NOT protected by [Authorize] - devices authenticate here to GET tokens.
/// </summary>
[ApiController]
[Route("api/device-auth")]
public class DeviceAuthController : ControllerBase
{
    private readonly IDeviceAuthenticationService _deviceAuthService;
    private readonly IDeviceService _deviceService;

    public DeviceAuthController(
        IDeviceAuthenticationService deviceAuthService,
        IDeviceService deviceService)
    {
        _deviceAuthService = deviceAuthService;
        _deviceService = deviceService;
    }

    /// <summary>
    /// Authenticate device using X.509 certificate (capable devices).
    /// The certificate PEM is typically extracted from mTLS by the edge gateway.
    /// </summary>
    [HttpPost("certificate")]
    public async Task<IActionResult> AuthenticateWithCertificate([FromBody] CertificateAuthRequest request)
    {
        var result = await _deviceAuthService.AuthenticateWithCertificateAsync(
            request.CertificatePem, request.DeviceId);

        if (!result.Success)
        {
            return Unauthorized(new { error = result.Error });
        }

        // Update device status with IP
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _deviceService.UpdateDeviceStatusAsync(request.DeviceId, true, ipAddress);

        return Ok(new
        {
            accessToken = result.AccessToken,
            tokenType = "Bearer",
            expiresIn = result.ExpiresIn,
            deviceId = result.DeviceId,
            permissions = result.Permissions
        });
    }

    /// <summary>
    /// Authenticate device using HMAC-SHA256 (constrained devices).
    /// Password format: "timestamp:nonce:hmac"
    /// </summary>
    [HttpPost("hmac")]
    public async Task<IActionResult> AuthenticateWithHmac([FromBody] HmacAuthRequest request)
    {
        var result = await _deviceAuthService.AuthenticateWithHmacAsync(
            request.DeviceId, request.Password);

        if (!result.Success)
        {
            return Unauthorized(new { error = result.Error });
        }

        // Update device status with IP
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _deviceService.UpdateDeviceStatusAsync(request.DeviceId, true, ipAddress);

        return Ok(new
        {
            accessToken = result.AccessToken,
            tokenType = "Bearer",
            expiresIn = result.ExpiresIn,
            deviceId = result.DeviceId,
            permissions = result.Permissions
        });
    }

    /// <summary>
    /// Authorize a device action on a resource.
    /// Used by edge gateways to check permissions before forwarding requests.
    /// </summary>
    [HttpPost("authorize")]
    public async Task<IActionResult> Authorize([FromBody] DeviceAuthorizeRequest request)
    {
        var result = await _deviceAuthService.AuthorizeAsync(
            request.DeviceId, request.Resource, request.Action);

        return Ok(new
        {
            allowed = result.Allowed,
            reason = result.Reason,
            matchedPermission = result.MatchedPermission
        });
    }

    /// <summary>
    /// Authorize MQTT publish/subscribe on a topic.
    /// Called by MQTT broker (e.g., Mosquitto) auth plugin.
    /// </summary>
    [HttpPost("authorize-mqtt")]
    public async Task<IActionResult> AuthorizeMqtt([FromBody] MqttAuthorizeRequest request)
    {
        var result = await _deviceAuthService.AuthorizeMqttAsync(
            request.DeviceId, request.Topic, request.Action);

        return Ok(new
        {
            allowed = result.Allowed,
            reason = result.Reason,
            matchedPermission = result.MatchedPermission
        });
    }

    /// <summary>
    /// Get device claims for edge gateway policy caching.
    /// Returns all permissions and metadata for local authorization decisions.
    /// </summary>
    [HttpGet("claims/{deviceId}")]
    public async Task<IActionResult> GetDeviceClaims(string deviceId)
    {
        var result = await _deviceAuthService.GetDeviceClaimsAsync(deviceId);

        if (!result.Success)
        {
            return NotFound(new { error = result.Error });
        }

        return Ok(new
        {
            deviceId = result.DeviceId,
            deviceType = result.DeviceType,
            resourcePath = result.ResourcePath,
            permissions = result.Permissions,
            metadata = result.Metadata,
            cacheTtlSeconds = 300 // 5-minute cache TTL for edge gateways
        });
    }

    /// <summary>
    /// Heartbeat endpoint for device status updates.
    /// Lightweight endpoint for constrained devices to report they're alive.
    /// </summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] DeviceHeartbeatRequest request)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var success = await _deviceService.UpdateDeviceStatusAsync(
            request.DeviceId, true, ipAddress);

        if (!success)
        {
            return NotFound(new { error = "Device not found" });
        }

        return Ok(new { status = "ok", timestamp = DateTime.UtcNow });
    }
}

// Request DTOs
public class CertificateAuthRequest
{
    public string CertificatePem { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
}

public class HmacAuthRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty; // Format: "timestamp:nonce:hmac"
}

public class DeviceAuthorizeRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}

public class MqttAuthorizeRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // "publish" or "subscribe"
}

public class DeviceHeartbeatRequest
{
    public string DeviceId { get; set; } = string.Empty;
}
