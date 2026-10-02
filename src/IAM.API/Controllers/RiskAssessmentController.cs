using System.Security.Claims;
using Hazina.Security.ApiKeys;
using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RiskAssessmentController : ControllerBase
{
    private readonly IRiskAssessmentService _riskService;
    private readonly ILogger<RiskAssessmentController> _logger;

    public RiskAssessmentController(IRiskAssessmentService riskService, ILogger<RiskAssessmentController> logger)
    {
        _riskService = riskService;
        _logger = logger;
    }

    // Task 4705. Thresholds decide when a login needs MFA or is blocked, so threshold management and the dashboard are
    // limited to the platform security roles. Scores, trusted devices and manual assessments are per-user data: a caller
    // may only act on their own user id (taken from the token; the request value must match or be omitted), anything
    // else needs a platform security role. Login-time evaluation (AuthService) uses the service directly and is unchanged.

    private const string PlatformSecurityRoles = "SuperAdmin,SecurityAdmin";

    private bool IsPlatformSecurityAdmin() => User.IsInRole("SuperAdmin") || User.IsInRole("SecurityAdmin");

    /// <summary>The signed-in user's id, or null for tokens that are not a user (API key, device, service account).</summary>
    private Guid? CallerUserId()
    {
        // An API key carries its issuing user's id, but it is a credential of its own, not that user.
        if (User.IsApiKey() || User.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return null;

        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id
            : null;
    }

    private ObjectResult Forbidden(string error = "You can only access your own risk data; acting on another user needs a platform security role.") =>
        StatusCode(StatusCodes.Status403Forbidden, new { error });

    /// <summary>
    /// Resolves which user an action applies to. A platform security admin may name any user (or default to
    /// themselves); everyone else gets their own id from the token, and naming a different user is 403.
    /// Returns the error result, or null with <paramref name="target"/> set.
    /// </summary>
    private ObjectResult? ResolveTargetUser(Guid? requested, out Guid target)
    {
        target = Guid.Empty;
        var caller = CallerUserId();
        var wanted = requested == Guid.Empty ? null : requested;

        if (IsPlatformSecurityAdmin() && !(User.IsApiKey() || User.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null))
        {
            var admin = wanted ?? caller;
            if (admin == null)
                return StatusCode(StatusCodes.Status400BadRequest, new { error = "UserId is required" });
            target = admin.Value;
            return null;
        }

        if (caller == null || (wanted != null && wanted != caller))
            return Forbidden();

        target = caller.Value;
        return null;
    }

    /// <summary>
    /// Thresholds that could never trigger (or would reorder the actions) would switch risk-based protection off.
    /// Scores run 0-100; a login is blocked at score &gt;= Block and steps up to MFA at score &gt; RequireMfaAbove, so
    /// RequireMfaAbove must be 0-99 and below Block, and Block must be 1-100.
    /// </summary>
    internal static string? ValidateThresholds(UpsertThresholdRequest r)
    {
        if (r.LowThreshold < 0 || r.LowThreshold >= r.MediumThreshold ||
            r.MediumThreshold >= r.HighThreshold ||
            r.HighThreshold >= r.BlockThreshold)
            return "Thresholds must be in ascending order: 0 <= Low < Medium < High < Block";

        if (r.BlockThreshold < 1 || r.BlockThreshold > 100)
            return "BlockThreshold must be between 1 and 100 (a higher value can never block a login)";

        if (r.RequireMfaAbove < 0 || r.RequireMfaAbove > 99)
            return "RequireMfaAbove must be between 0 and 99 (scores never exceed 100, so a higher value can never require MFA)";

        if (r.RequireMfaAbove >= r.BlockThreshold)
            return "RequireMfaAbove must be below BlockThreshold";

        return null;
    }

    // ===========================================
    // Dashboard & Scores
    // ===========================================

    /// <summary>
    /// Get risk dashboard data: heatmap, distribution, and summary statistics.
    /// </summary>
    [HttpGet("dashboard")]
    [Authorize(Roles = PlatformSecurityRoles)]
    public async Task<ActionResult<RiskDashboardData>> GetDashboard(
        [FromQuery] Guid? tenantId,
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        if (days < 1 || days > 365)
            return BadRequest("Days must be between 1 and 365");

        var data = await _riskService.GetDashboardDataAsync(tenantId, days, cancellationToken);
        return Ok(data);
    }

    /// <summary>
    /// Get risk score history, optionally filtered by user.
    /// </summary>
    [HttpGet("scores")]
    public async Task<ActionResult<List<LoginRiskScore>>> GetScores(
        [FromQuery] Guid? userId,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        // Platform security admins may list everyone's scores (userId omitted) or one user's; others only their own.
        Guid? scopeUser = userId;
        if (!IsPlatformSecurityAdmin() || userId.HasValue)
        {
            if (ResolveTargetUser(userId, out var target) is { } denied)
                return denied;
            scopeUser = target;
        }

        var scores = await _riskService.GetRiskScoresAsync(scopeUser, startDate, endDate, skip, take, cancellationToken);
        return Ok(scores);
    }

    /// <summary>
    /// Manually assess risk for a login attempt (for testing/admin purposes).
    /// </summary>
    [HttpPost("assess")]
    public async Task<ActionResult<LoginRiskScore>> AssessRisk(
        [FromBody] AssessRiskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (ResolveTargetUser(request.UserId, out var assessUser) is { } denied)
            return denied;

        if (string.IsNullOrWhiteSpace(request.IpAddress))
            return BadRequest("IpAddress is required");

        var result = await _riskService.AssessLoginRiskAsync(
            assessUser,
            request.IpAddress,
            request.UserAgent,
            request.DeviceFingerprint,
            cancellationToken);

        return Ok(result);
    }

    // ===========================================
    // Risk Thresholds CRUD
    // ===========================================

    /// <summary>
    /// Get all risk threshold configurations.
    /// </summary>
    [HttpGet("thresholds")]
    [Authorize(Roles = PlatformSecurityRoles)]
    public async Task<ActionResult<List<RiskThreshold>>> GetThresholds(
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var thresholds = await _riskService.GetThresholdsAsync(tenantId, cancellationToken);
        return Ok(thresholds);
    }

    /// <summary>
    /// Get a single risk threshold by ID.
    /// </summary>
    [HttpGet("thresholds/{id:guid}")]
    [Authorize(Roles = PlatformSecurityRoles)]
    public async Task<ActionResult<RiskThreshold>> GetThreshold(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var threshold = await _riskService.GetThresholdByIdAsync(id, cancellationToken);
        if (threshold == null) return NotFound();
        return Ok(threshold);
    }

    /// <summary>
    /// Create or update a risk threshold configuration.
    /// </summary>
    [HttpPost("thresholds")]
    [Authorize(Roles = PlatformSecurityRoles)]
    public async Task<ActionResult<RiskThreshold>> UpsertThreshold(
        [FromBody] UpsertThresholdRequest request,
        CancellationToken cancellationToken = default)
    {
        if (ValidateThresholds(request) is { } invalid)
            return BadRequest(invalid);

        var threshold = new RiskThreshold
        {
            Id = request.Id ?? Guid.NewGuid(),
            TenantId = request.TenantId,
            LowThreshold = request.LowThreshold,
            MediumThreshold = request.MediumThreshold,
            HighThreshold = request.HighThreshold,
            BlockThreshold = request.BlockThreshold,
            RequireMfaAbove = request.RequireMfaAbove,
            IsActive = request.IsActive
        };

        var result = await _riskService.UpsertThresholdAsync(threshold, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Delete a risk threshold configuration.
    /// </summary>
    [HttpDelete("thresholds/{id:guid}")]
    [Authorize(Roles = PlatformSecurityRoles)]
    public async Task<ActionResult> DeleteThreshold(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _riskService.DeleteThresholdAsync(id, cancellationToken);
        if (!deleted) return NotFound();
        return NoContent();
    }

    // ===========================================
    // Trusted Devices CRUD
    // ===========================================

    /// <summary>
    /// Get trusted devices for a user.
    /// </summary>
    [HttpGet("devices/{userId:guid}")]
    public async Task<ActionResult<List<TrustedDevice>>> GetTrustedDevices(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (ResolveTargetUser(userId, out var owner) is { } denied)
            return denied;

        var devices = await _riskService.GetTrustedDevicesAsync(owner, cancellationToken);
        return Ok(devices);
    }

    /// <summary>
    /// Register a device as trusted.
    /// </summary>
    [HttpPost("devices")]
    public async Task<ActionResult<TrustedDevice>> TrustDevice(
        [FromBody] TrustDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (ResolveTargetUser(request.UserId, out var deviceOwner) is { } denied)
            return denied;

        if (string.IsNullOrWhiteSpace(request.DeviceFingerprint))
            return BadRequest("DeviceFingerprint is required");

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required");

        var device = await _riskService.TrustDeviceAsync(
            deviceOwner,
            request.DeviceFingerprint,
            request.Name,
            cancellationToken);

        return Ok(device);
    }

    /// <summary>
    /// Remove a trusted device.
    /// </summary>
    [HttpDelete("devices/{id:guid}")]
    public async Task<ActionResult> RemoveTrustedDevice(
        Guid id,
        [FromQuery] Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (ResolveTargetUser(userId, out var owner) is { } denied)
            return denied;

        var removed = await _riskService.RemoveTrustedDeviceAsync(id, owner, cancellationToken);
        if (!removed) return NotFound();
        return NoContent();
    }
}

// ===========================================
// Request DTOs
// ===========================================

public class AssessRiskRequest
{
    /// <summary>Omit (or send the caller's own id) to assess yourself; another user needs a platform security role.</summary>
    public Guid UserId { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? DeviceFingerprint { get; set; }
}

public class UpsertThresholdRequest
{
    public Guid? Id { get; set; }
    public Guid? TenantId { get; set; }
    public int LowThreshold { get; set; } = 20;
    public int MediumThreshold { get; set; } = 50;
    public int HighThreshold { get; set; } = 75;
    public int BlockThreshold { get; set; } = 90;
    public int RequireMfaAbove { get; set; } = 40;
    public bool IsActive { get; set; } = true;
}

public class TrustDeviceRequest
{
    /// <summary>Omit (or send the caller's own id) for your own devices; another user needs a platform security role.</summary>
    public Guid UserId { get; set; }
    public string DeviceFingerprint { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
