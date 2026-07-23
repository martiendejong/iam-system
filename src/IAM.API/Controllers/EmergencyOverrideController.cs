using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EmergencyOverrideController : ControllerBase
{
    private readonly IEmergencyOverrideService _overrideService;
    private readonly ILogger<EmergencyOverrideController> _logger;

    public EmergencyOverrideController(
        IEmergencyOverrideService overrideService,
        ILogger<EmergencyOverrideController> logger)
    {
        _overrideService = overrideService;
        _logger = logger;
    }

    /// <summary>
    /// Activate emergency override (break-glass access)
    /// </summary>
    [HttpPost("activate")]
    [Authorize(Roles = "EmergencyAccess,SystemAdmin")]
    public async Task<ActionResult<EmergencyOverride>> ActivateOverride(
        [FromBody] ActivateOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Justification))
            return BadRequest("Justification is required");

        // Get user ID from claims (try multiple claim types)
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        // Get IP address from request
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        try
        {
            var emergencyOverride = await _overrideService.ActivateOverrideAsync(
                userId,
                request.TenantId,
                request.OverrideType,
                request.Justification,
                request.Severity,
                request.DurationMinutes,
                request.IncidentTicketId,
                ipAddress,
                request.DeviceId,
                cancellationToken);

            return Ok(emergencyOverride);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Deactivate emergency override
    /// </summary>
    [HttpPost("{id}/deactivate")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<EmergencyOverride>> DeactivateOverride(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // Get user ID from claims (try multiple claim types)
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        try
        {
            var emergencyOverride = await _overrideService.DeactivateOverrideAsync(
                id, userId, cancellationToken);

            return Ok(emergencyOverride);
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Check if user has active override
    /// </summary>
    [HttpGet("active/check")]
    public async Task<ActionResult<bool>> HasActiveOverride(
        [FromQuery] Guid userId,
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var hasActive = await _overrideService.HasActiveOverrideAsync(
            userId, tenantId, cancellationToken);

        return Ok(new { HasActiveOverride = hasActive });
    }

    /// <summary>
    /// Get active emergency overrides
    /// </summary>
    [HttpGet("active")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<List<EmergencyOverride>>> GetActiveOverrides(
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var overrides = await _overrideService.GetActiveOverridesAsync(
            tenantId, cancellationToken);

        return Ok(overrides);
    }

    /// <summary>
    /// Get emergency override history
    /// </summary>
    [HttpGet("history")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<List<EmergencyOverride>>> GetOverrideHistory(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? tenantId,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var history = await _overrideService.GetOverrideHistoryAsync(
            userId, tenantId, startDate, endDate, skip, take, cancellationToken);

        return Ok(history);
    }

    /// <summary>
    /// Review emergency override (post-incident)
    /// </summary>
    [HttpPost("{id}/review")]
    [Authorize(Roles = "SecurityAdmin,ComplianceOfficer")]
    public async Task<ActionResult<EmergencyOverride>> ReviewOverride(
        Guid id,
        [FromBody] ReviewOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        // Get user ID from claims (try multiple claim types)
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        try
        {
            var emergencyOverride = await _overrideService.ReviewOverrideAsync(
                id,
                userId,
                request.ApprovalStatus,
                request.ReviewComments,
                cancellationToken);

            return Ok(emergencyOverride);
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Get overrides pending review
    /// </summary>
    [HttpGet("pending-review")]
    [Authorize(Roles = "SecurityAdmin,ComplianceOfficer")]
    public async Task<ActionResult<List<EmergencyOverride>>> GetPendingReviews(
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var pending = await _overrideService.GetPendingReviewsAsync(
            tenantId, cancellationToken);

        return Ok(pending);
    }
}

public class ActivateOverrideRequest
{
    public Guid TenantId { get; set; }
    public EmergencyOverrideType OverrideType { get; set; }
    public string Justification { get; set; } = string.Empty;
    public EmergencySeverity Severity { get; set; } = EmergencySeverity.Medium;
    public int DurationMinutes { get; set; } = 60;
    public string? IncidentTicketId { get; set; }
    public string? DeviceId { get; set; }
}

public class ReviewOverrideRequest
{
    public EmergencyApprovalStatus ApprovalStatus { get; set; }
    public string? ReviewComments { get; set; }
}
