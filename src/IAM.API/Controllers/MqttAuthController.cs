using IAM.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// MQTT Broker Authentication Plugin endpoints.
/// Compatible with Mosquitto (mosquitto-auth-plug), EMQX (HTTP auth), and HiveMQ (HTTP extension).
/// Brokers call these endpoints to authenticate/authorize MQTT clients.
///
/// NOT [Authorize] - MQTT brokers authenticate via X-Broker-Secret shared header.
/// </summary>
[ApiController]
[Route("api/mqtt")]
public class MqttAuthController : ControllerBase
{
    private readonly IDeviceAuthenticationService _deviceAuthService;
    private readonly IMqttAuthService _mqttAuthService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttAuthController> _logger;

    public MqttAuthController(
        IDeviceAuthenticationService deviceAuthService,
        IMqttAuthService mqttAuthService,
        IConfiguration configuration,
        ILogger<MqttAuthController> logger)
    {
        _deviceAuthService = deviceAuthService;
        _mqttAuthService = mqttAuthService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Validate the broker shared secret from the X-Broker-Secret header.
    /// If no secret is configured (dev mode), all requests are allowed.
    /// </summary>
    private bool ValidateBrokerSecret()
    {
        var expectedSecret = _configuration["Mqtt:BrokerSecret"];
        if (string.IsNullOrEmpty(expectedSecret))
            return true; // No secret configured = open (dev mode)

        var provided = Request.Headers["X-Broker-Secret"].FirstOrDefault();
        return provided == expectedSecret;
    }

    // ─────────────────────────────────────────────────────────────────
    // Mosquitto-compatible endpoints (mosquitto-auth-plug format)
    // Returns HTTP 200 for allow, HTTP 403 for deny
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mosquitto-compatible auth endpoint.
    /// Called on every MQTT CONNECT to authenticate the client.
    /// Returns 200 for allow, 403 for deny.
    /// </summary>
    [HttpPost("auth")]
    public async Task<IActionResult> Authenticate([FromBody] MqttAuthRequest request)
    {
        if (!ValidateBrokerSecret())
        {
            _logger.LogWarning("MQTT auth request rejected: invalid broker secret");
            return StatusCode(403);
        }

        var result = await _mqttAuthService.AuthenticateClientAsync(
            request.ClientId, request.Username, request.Password);

        if (!result.Allowed)
        {
            _logger.LogInformation("MQTT auth denied for {ClientId}: {Error}", request.ClientId, result.Error);
            return StatusCode(403);
        }

        // Record the connection
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _mqttAuthService.RecordConnectionAsync(request.ClientId, ipAddress);

        return Ok();
    }

    /// <summary>
    /// Mosquitto-compatible superuser check.
    /// Superusers bypass ACL checks entirely.
    /// Returns 200 for superuser, 403 for non-superuser.
    /// </summary>
    [HttpPost("superuser")]
    public async Task<IActionResult> SuperuserCheck([FromBody] MqttSuperuserRequest request)
    {
        if (!ValidateBrokerSecret())
            return StatusCode(403);

        var isSuperuser = await _mqttAuthService.IsSuperuserAsync(request.ClientId);

        if (isSuperuser)
        {
            _logger.LogDebug("MQTT superuser confirmed: {ClientId}", request.ClientId);
            return Ok();
        }

        return StatusCode(403);
    }

    /// <summary>
    /// Mosquitto-compatible ACL check.
    /// Called on every PUBLISH/SUBSCRIBE to authorize topic access.
    /// Acc field: 1 = subscribe, 2 = publish (Mosquitto convention).
    /// Returns 200 for allow, 403 for deny.
    /// </summary>
    [HttpPost("acl")]
    public async Task<IActionResult> AclCheck([FromBody] MqttAclRequest request)
    {
        if (!ValidateBrokerSecret())
            return StatusCode(403);

        // Mosquitto convention: acc 1 = subscribe, 2 = publish
        var action = request.Acc switch
        {
            1 => MqttAclAction.Subscribe,
            2 => MqttAclAction.Publish,
            _ => MqttAclAction.Subscribe
        };

        var result = await _mqttAuthService.CheckAclAsync(request.ClientId, request.Topic, action);

        if (result.Allowed)
            return Ok();

        _logger.LogInformation("MQTT ACL denied: {ClientId} acc={Acc} topic={Topic} - {Reason}",
            request.ClientId, request.Acc, request.Topic, result.Reason);
        return StatusCode(403);
    }

    // ─────────────────────────────────────────────────────────────────
    // EMQX-compatible endpoints (HTTP auth plugin format)
    // Returns JSON with "result" field: "allow", "deny", or "ignore"
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// EMQX-compatible combined auth endpoint.
    /// Returns JSON: { "result": "allow" } or { "result": "deny" }
    /// </summary>
    [HttpPost("emqx/auth")]
    public async Task<IActionResult> EmqxAuth([FromBody] EmqxAuthRequest request)
    {
        if (!ValidateBrokerSecret())
            return Ok(new { result = "deny" });

        var result = await _mqttAuthService.AuthenticateClientAsync(
            request.ClientId, request.Username, request.Password);

        if (!result.Allowed)
        {
            _logger.LogInformation("EMQX auth denied for {ClientId}: {Error}", request.ClientId, result.Error);
            return Ok(new { result = "deny" });
        }

        // Record connection with peer host from EMQX
        await _mqttAuthService.RecordConnectionAsync(request.ClientId, request.PeerHost);

        return Ok(new
        {
            result = "allow",
            is_superuser = await _mqttAuthService.IsSuperuserAsync(request.ClientId)
        });
    }

    /// <summary>
    /// EMQX-compatible ACL endpoint.
    /// Action field: "publish" or "subscribe".
    /// Returns JSON: { "result": "allow" } or { "result": "deny" }
    /// </summary>
    [HttpPost("emqx/acl")]
    public async Task<IActionResult> EmqxAcl([FromBody] EmqxAclRequest request)
    {
        if (!ValidateBrokerSecret())
            return Ok(new { result = "deny" });

        var action = request.Action?.ToLowerInvariant() switch
        {
            "publish" => MqttAclAction.Publish,
            "subscribe" => MqttAclAction.Subscribe,
            _ => MqttAclAction.Subscribe
        };

        var result = await _mqttAuthService.CheckAclAsync(request.ClientId, request.Topic, action);

        if (result.Allowed)
        {
            return Ok(new { result = "allow" });
        }

        _logger.LogInformation("EMQX ACL denied: {ClientId} {Action} {Topic} - {Reason}",
            request.ClientId, request.Action, request.Topic, result.Reason);
        return Ok(new { result = "deny" });
    }

    // ─────────────────────────────────────────────────────────────────
    // Statistics & lifecycle
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get MQTT connection and authentication statistics.
    /// Useful for monitoring dashboards.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!ValidateBrokerSecret())
            return StatusCode(403);

        var stats = await _mqttAuthService.GetStatsAsync();

        return Ok(new
        {
            connectedDevices = stats.ConnectedDevices,
            totalConnections = stats.TotalConnections,
            totalAuthAttempts = stats.TotalAuthAttempts,
            failedAuthAttempts = stats.FailedAuthAttempts,
            aclChecks = stats.AclChecks,
            aclDenied = stats.AclDenied,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Webhook endpoint for broker disconnect notifications.
    /// Called by MQTT brokers when a client disconnects (if webhook is configured).
    /// </summary>
    [HttpPost("disconnect")]
    public async Task<IActionResult> ClientDisconnected([FromBody] MqttDisconnectRequest request)
    {
        if (!ValidateBrokerSecret())
            return StatusCode(403);

        await _mqttAuthService.RecordDisconnectionAsync(request.ClientId);
        return Ok();
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Request DTOs - Mosquitto format
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Mosquitto auth request. Sent on MQTT CONNECT.
/// </summary>
public class MqttAuthRequest
{
    /// <summary>MQTT client identifier (maps to Device.DeviceId)</summary>
    public string ClientId { get; set; } = "";

    /// <summary>MQTT username (optional, "certificate" for cert-based auth)</summary>
    public string? Username { get; set; }

    /// <summary>MQTT password (for HMAC: "timestamp:nonce:hmac" format)</summary>
    public string? Password { get; set; }
}

/// <summary>
/// Mosquitto superuser check request.
/// </summary>
public class MqttSuperuserRequest
{
    /// <summary>MQTT client identifier</summary>
    public string ClientId { get; set; } = "";

    /// <summary>MQTT username</summary>
    public string? Username { get; set; }
}

/// <summary>
/// Mosquitto ACL check request. Sent on PUBLISH/SUBSCRIBE.
/// </summary>
public class MqttAclRequest
{
    /// <summary>MQTT client identifier</summary>
    public string ClientId { get; set; } = "";

    /// <summary>MQTT username</summary>
    public string? Username { get; set; }

    /// <summary>MQTT topic being accessed (e.g., "acme/headquarters/floor-3/hvac/unit-247/telemetry")</summary>
    public string Topic { get; set; } = "";

    /// <summary>Access type: 1 = subscribe, 2 = publish (Mosquitto convention)</summary>
    public int Acc { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────
// Request DTOs - EMQX format
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// EMQX HTTP auth request. Sent on MQTT CONNECT.
/// </summary>
public class EmqxAuthRequest
{
    /// <summary>MQTT client identifier</summary>
    public string ClientId { get; set; } = "";

    /// <summary>MQTT username</summary>
    public string? Username { get; set; }

    /// <summary>MQTT password</summary>
    public string? Password { get; set; }

    /// <summary>Client IP address as seen by EMQX</summary>
    public string? PeerHost { get; set; }
}

/// <summary>
/// EMQX HTTP ACL request. Sent on PUBLISH/SUBSCRIBE.
/// </summary>
public class EmqxAclRequest
{
    /// <summary>MQTT client identifier</summary>
    public string ClientId { get; set; } = "";

    /// <summary>MQTT username</summary>
    public string? Username { get; set; }

    /// <summary>MQTT topic being accessed</summary>
    public string Topic { get; set; } = "";

    /// <summary>Action: "publish" or "subscribe"</summary>
    public string Action { get; set; } = "";
}

// ─────────────────────────────────────────────────────────────────────────
// Request DTOs - Disconnect webhook
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Disconnect notification from the MQTT broker.
/// </summary>
public class MqttDisconnectRequest
{
    /// <summary>MQTT client identifier</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Reason for disconnect (optional)</summary>
    public string? Reason { get; set; }
}
