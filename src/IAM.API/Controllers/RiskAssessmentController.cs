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

    // ===========================================
    // Dashboard & Scores
    // ===========================================

    /// <summary>
    /// Get risk dashboard data: heatmap, distribution, and summary statistics.
    /// </summary>
    [HttpGet("dashboard")]
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
        var scores = await _riskService.GetRiskScoresAsync(userId, startDate, endDate, skip, take, cancellationToken);
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
        if (request.UserId == Guid.Empty)
            return BadRequest("UserId is required");

        if (string.IsNullOrWhiteSpace(request.IpAddress))
            return BadRequest("IpAddress is required");

        var result = await _riskService.AssessLoginRiskAsync(
            request.UserId,
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
    public async Task<ActionResult<RiskThreshold>> UpsertThreshold(
        [FromBody] UpsertThresholdRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.LowThreshold >= request.MediumThreshold ||
            request.MediumThreshold >= request.HighThreshold ||
            request.HighThreshold >= request.BlockThreshold)
        {
            return BadRequest("Thresholds must be in ascending order: Low < Medium < High < Block");
        }

        if (request.RequireMfaAbove < 0 || request.RequireMfaAbove > 100)
            return BadRequest("RequireMfaAbove must be between 0 and 100");

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
        var devices = await _riskService.GetTrustedDevicesAsync(userId, cancellationToken);
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
        if (request.UserId == Guid.Empty)
            return BadRequest("UserId is required");

        if (string.IsNullOrWhiteSpace(request.DeviceFingerprint))
            return BadRequest("DeviceFingerprint is required");

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required");

        var device = await _riskService.TrustDeviceAsync(
            request.UserId,
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
        [FromQuery] Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return BadRequest("UserId query parameter is required");

        var removed = await _riskService.RemoveTrustedDeviceAsync(id, userId, cancellationToken);
        if (!removed) return NotFound();
        return NoContent();
    }
}

// ===========================================
// Request DTOs
// ===========================================

public class AssessRiskRequest
{
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
    public Guid UserId { get; set; }
    public string DeviceFingerprint { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
