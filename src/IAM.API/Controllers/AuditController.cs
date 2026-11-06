using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly IAuditService _auditService;
    private readonly IAuditAccessResolver _accessResolver;
    private readonly ILogger<AuditController> _logger;

    public AuditController(
        IAuditService auditService,
        IAuditAccessResolver accessResolver,
        ILogger<AuditController> logger)
    {
        _auditService = auditService;
        _accessResolver = accessResolver;
        _logger = logger;
    }

    /// <summary>
    /// Maps a non-allowed <see cref="AuditAccessResult"/> to its HTTP response (403 / 400).
    /// </summary>
    private ActionResult DenyAccess(AuditAccessResult access) =>
        access.Decision == AuditAccessDecision.TenantRequired
            ? BadRequest(new { error = access.Message })
            : StatusCode(StatusCodes.Status403Forbidden, new { error = access.Message });

    /// <summary>
    /// Get audit events with filtering. Administrators only; tenant admins are limited to a
    /// tenant they administer, SuperAdmin / SecurityAdmin may query any tenant (task 4714).
    /// </summary>
    [HttpGet("events")]
    public async Task<ActionResult<List<PolicyAuditEvent>>> GetAuditEvents(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] Guid? userId,
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? policyId,
        [FromQuery] PolicyAuditEventType? eventType,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var access = await _accessResolver.ResolveAsync(User, tenantId, cancellationToken);
        if (access.Decision != AuditAccessDecision.Allowed)
            return DenyAccess(access);

        var events = await _auditService.GetAuditEventsAsync(
            startDate, endDate, userId, access.TenantId, policyId, eventType,
            skip, take, cancellationToken);

        return Ok(events);
    }

    /// <summary>
    /// Get compliance statistics for a time period (same access rules as the events list)
    /// </summary>
    [HttpGet("statistics")]
    public async Task<ActionResult<ComplianceStatistics>> GetComplianceStatistics(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var access = await _accessResolver.ResolveAsync(User, tenantId, cancellationToken);
        if (access.Decision != AuditAccessDecision.Allowed)
            return DenyAccess(access);

        if (startDate == default || endDate == default)
            return BadRequest("Start date and end date are required");

        var statistics = await _auditService.GetComplianceStatisticsAsync(
            startDate, endDate, access.TenantId, cancellationToken);

        return Ok(statistics);
    }

    /// <summary>
    /// Generate compliance report. A report can only be generated for a tenant the caller
    /// administers; SuperAdmin / SecurityAdmin may also generate an all-tenant report.
    /// </summary>
    [HttpPost("reports")]
    public async Task<ActionResult<ComplianceReport>> GenerateComplianceReport(
        [FromBody] GenerateReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var access = await _accessResolver.ResolveAsync(User, request.TenantId, cancellationToken);
        if (access.Decision != AuditAccessDecision.Allowed)
            return DenyAccess(access);

        if (string.IsNullOrWhiteSpace(request.Framework))
            return BadRequest("Framework is required");

        // Get user ID from claims (try multiple claim types)
        var userIdClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        var report = await _auditService.GenerateComplianceReportAsync(
            request.Framework,
            request.PeriodStart,
            request.PeriodEnd,
            access.TenantId,
            userId,
            cancellationToken);

        return Ok(report);
    }

    /// <summary>
    /// Detect anomalous access patterns
    /// </summary>
    [HttpGet("anomalies")]
    [Authorize(Roles = "SecurityAdmin")]
    public async Task<ActionResult<List<PolicyAuditEvent>>> DetectAnomalies(
        [FromQuery] DateTime? since,
        CancellationToken cancellationToken = default)
    {
        var sinceDate = since ?? DateTime.UtcNow.AddDays(-7);
        var anomalies = await _auditService.DetectAnomaliesAsync(sinceDate, cancellationToken);

        return Ok(anomalies);
    }

    /// <summary>
    /// Clean up old audit events (admin only)
    /// </summary>
    [HttpDelete("cleanup")]
    [Authorize(Roles = "SystemAdmin")]
    public async Task<ActionResult<int>> CleanupOldAuditEvents(
        [FromQuery] int retentionDays = 90,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays < 30 || retentionDays > 3650)
            return BadRequest("Retention days must be between 30 and 3650");

        var deletedCount = await _auditService.CleanupOldAuditEventsAsync(
            retentionDays, cancellationToken);

        return Ok(new { DeletedCount = deletedCount });
    }
}

public class GenerateReportRequest
{
    public string Framework { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public Guid? TenantId { get; set; }
}
