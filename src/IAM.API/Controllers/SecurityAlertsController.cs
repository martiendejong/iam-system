using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SecurityAlertsController : ControllerBase
{
    private readonly ISecurityAlertService _alertService;
    private readonly ILogger<SecurityAlertsController> _logger;

    public SecurityAlertsController(ISecurityAlertService alertService, ILogger<SecurityAlertsController> logger)
    {
        _alertService = alertService;
        _logger = logger;
    }

    // ────────────────────────────────────────────────────────────
    //  Alert Rules
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Get all alert rules (optionally filtered by tenant)
    /// </summary>
    [HttpGet("rules")]
    public async Task<ActionResult<List<AlertRule>>> GetRules(
        [FromQuery] Guid? tenantId,
        CancellationToken ct = default)
    {
        var rules = await _alertService.GetRulesAsync(tenantId, ct);
        return Ok(rules);
    }

    /// <summary>
    /// Get a single alert rule by ID
    /// </summary>
    [HttpGet("rules/{id:guid}")]
    public async Task<ActionResult<AlertRule>> GetRule(Guid id, CancellationToken ct = default)
    {
        var rule = await _alertService.GetRuleAsync(id, ct);
        if (rule == null) return NotFound();
        return Ok(rule);
    }

    /// <summary>
    /// Create a new alert rule
    /// </summary>
    [HttpPost("rules")]
    public async Task<ActionResult<AlertRule>> CreateRule(
        [FromBody] CreateAlertRuleRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Rule name is required");

        var rule = new AlertRule
        {
            TenantId = request.TenantId,
            Name = request.Name,
            Condition = request.Condition ?? "{}",
            Severity = request.Severity,
            Channels = request.Channels ?? "[]",
            CooldownMinutes = request.CooldownMinutes,
            AutoResponseAction = request.AutoResponseAction,
            IsActive = request.IsActive
        };

        var created = await _alertService.CreateRuleAsync(rule, ct);
        return CreatedAtAction(nameof(GetRule), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an alert rule
    /// </summary>
    [HttpPut("rules/{id:guid}")]
    public async Task<ActionResult<AlertRule>> UpdateRule(
        Guid id,
        [FromBody] CreateAlertRuleRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var rule = new AlertRule
            {
                Name = request.Name,
                Condition = request.Condition ?? "{}",
                Severity = request.Severity,
                Channels = request.Channels ?? "[]",
                CooldownMinutes = request.CooldownMinutes,
                AutoResponseAction = request.AutoResponseAction,
                IsActive = request.IsActive
            };

            var updated = await _alertService.UpdateRuleAsync(id, rule, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete an alert rule
    /// </summary>
    [HttpDelete("rules/{id:guid}")]
    public async Task<ActionResult> DeleteRule(Guid id, CancellationToken ct = default)
    {
        var deleted = await _alertService.DeleteRuleAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    // ────────────────────────────────────────────────────────────
    //  Active Alerts
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Get all active (unacknowledged) alerts
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<SecurityAlert>>> GetActiveAlerts(
        [FromQuery] Guid? tenantId,
        CancellationToken ct = default)
    {
        var alerts = await _alertService.GetActiveAlertsAsync(tenantId, ct);
        return Ok(alerts);
    }

    /// <summary>
    /// Get alert history (all alerts, including acknowledged)
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<List<SecurityAlert>>> GetAlertHistory(
        [FromQuery] Guid? tenantId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var alerts = await _alertService.GetAlertHistoryAsync(tenantId, skip, take, ct);
        return Ok(alerts);
    }

    /// <summary>
    /// Acknowledge a security alert
    /// </summary>
    [HttpPost("{id:guid}/acknowledge")]
    public async Task<ActionResult<SecurityAlert>> AcknowledgeAlert(Guid id, CancellationToken ct = default)
    {
        var userIdClaim = User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        var alert = await _alertService.AcknowledgeAlertAsync(id, userId, ct);
        if (alert == null) return NotFound();
        return Ok(alert);
    }

    // ────────────────────────────────────────────────────────────
    //  SIEM Integrations
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Get all SIEM integrations
    /// </summary>
    [HttpGet("siem")]
    public async Task<ActionResult<List<SiemIntegration>>> GetSiemIntegrations(
        [FromQuery] Guid? tenantId,
        CancellationToken ct = default)
    {
        var integrations = await _alertService.GetSiemIntegrationsAsync(tenantId, ct);
        return Ok(integrations);
    }

    /// <summary>
    /// Get a SIEM integration by ID
    /// </summary>
    [HttpGet("siem/{id:guid}")]
    public async Task<ActionResult<SiemIntegration>> GetSiemIntegration(Guid id, CancellationToken ct = default)
    {
        var integration = await _alertService.GetSiemIntegrationAsync(id, ct);
        if (integration == null) return NotFound();
        return Ok(integration);
    }

    /// <summary>
    /// Create a SIEM integration
    /// </summary>
    [HttpPost("siem")]
    public async Task<ActionResult<SiemIntegration>> CreateSiemIntegration(
        [FromBody] CreateSiemIntegrationRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Integration name is required");

        if (string.IsNullOrWhiteSpace(request.EndpointUrl))
            return BadRequest("Endpoint URL is required");

        var integration = new SiemIntegration
        {
            TenantId = request.TenantId,
            Name = request.Name,
            Type = request.Type,
            EndpointUrl = request.EndpointUrl,
            AuthConfig = request.AuthConfig,
            Format = request.Format ?? "JSON",
            EventFilter = request.EventFilter,
            IsActive = request.IsActive
        };

        var created = await _alertService.CreateSiemIntegrationAsync(integration, ct);
        return CreatedAtAction(nameof(GetSiemIntegration), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update a SIEM integration
    /// </summary>
    [HttpPut("siem/{id:guid}")]
    public async Task<ActionResult<SiemIntegration>> UpdateSiemIntegration(
        Guid id,
        [FromBody] CreateSiemIntegrationRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var integration = new SiemIntegration
            {
                Name = request.Name,
                Type = request.Type,
                EndpointUrl = request.EndpointUrl,
                AuthConfig = request.AuthConfig,
                Format = request.Format ?? "JSON",
                EventFilter = request.EventFilter,
                IsActive = request.IsActive
            };

            var updated = await _alertService.UpdateSiemIntegrationAsync(id, integration, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a SIEM integration
    /// </summary>
    [HttpDelete("siem/{id:guid}")]
    public async Task<ActionResult> DeleteSiemIntegration(Guid id, CancellationToken ct = default)
    {
        var deleted = await _alertService.DeleteSiemIntegrationAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}

// ── Request DTOs ─────────────────────────────────────────────────

public class CreateAlertRuleRequest
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Condition { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;
    public string? Channels { get; set; }
    public int CooldownMinutes { get; set; } = 15;
    public string? AutoResponseAction { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CreateSiemIntegrationRequest
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public SiemType Type { get; set; }
    public string EndpointUrl { get; set; } = string.Empty;
    public string? AuthConfig { get; set; }
    public string? Format { get; set; }
    public string? EventFilter { get; set; }
    public bool IsActive { get; set; } = true;
}
