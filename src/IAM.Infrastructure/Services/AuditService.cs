using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Implementation of audit logging and compliance tracking service
/// </summary>
public class AuditService : IAuditService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IAMDbContext context, ILogger<AuditService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task LogPolicyEventAsync(
        PolicyAuditEventType eventType,
        Guid? policyId = null,
        Guid? userId = null,
        Guid? tenantId = null,
        string? resource = null,
        string? action = null,
        bool? wasAllowed = null,
        string? reason = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var auditEvent = new PolicyAuditEvent
        {
            EventType = eventType,
            PolicyId = policyId,
            UserId = userId,
            TenantId = tenantId,
            Resource = resource,
            Action = action,
            WasAllowed = wasAllowed,
            Reason = reason,
            Metadata = metadata != null ? JsonSerializer.Serialize(metadata) : null
        };

        _context.PolicyAuditEvents.Add(auditEvent);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Audit event logged: {EventType} for User:{UserId} Resource:{Resource} Action:{Action} Allowed:{Allowed}",
            eventType, userId, resource, action, wasAllowed);
    }

    public async Task LogPolicyEvaluationAsync(
        PolicyEvaluationResult result,
        Guid userId,
        Guid tenantId,
        string resource,
        string action,
        string? ipAddress = null,
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var eventType = result.IsAllowed
            ? PolicyAuditEventType.AccessGranted
            : PolicyAuditEventType.AccessDenied;

        await LogPolicyEventAsync(
            eventType,
            policyId: result.PolicyId,
            userId: userId,
            tenantId: tenantId,
            resource: resource,
            action: action,
            wasAllowed: result.IsAllowed,
            reason: result.Reason,
            metadata: new Dictionary<string, object>
            {
                ["IpAddress"] = ipAddress ?? "unknown",
                ["DeviceId"] = deviceId ?? "unknown",
                ["EvaluatedPolicies"] = result.EvaluatedPoliciesCount,
                ["EvaluationTimeMs"] = result.EvaluationTimeMs
            },
            cancellationToken: cancellationToken);
    }

    public async Task<List<PolicyAuditEvent>> GetAuditEventsAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        Guid? userId = null,
        Guid? tenantId = null,
        Guid? policyId = null,
        PolicyAuditEventType? eventType = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var query = _context.PolicyAuditEvents.AsQueryable();

        if (startDate.HasValue)
            query = query.Where(e => e.CreatedAt >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(e => e.CreatedAt <= endDate.Value);

        if (userId.HasValue)
            query = query.Where(e => e.UserId == userId.Value);

        if (tenantId.HasValue)
            query = query.Where(e => e.TenantId == tenantId.Value);

        if (policyId.HasValue)
            query = query.Where(e => e.PolicyId == policyId.Value);

        if (eventType.HasValue)
            query = query.Where(e => e.EventType == eventType.Value);

        return await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip)
            .Take(Math.Min(take, 1000)) // Max 1000 per query
            .ToListAsync(cancellationToken);
    }

    public async Task<ComplianceStatistics> GetComplianceStatisticsAsync(
        DateTime startDate,
        DateTime endDate,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var eventsQuery = _context.PolicyAuditEvents
            .Where(e => e.CreatedAt >= startDate && e.CreatedAt <= endDate);

        if (tenantId.HasValue)
            eventsQuery = eventsQuery.Where(e => e.TenantId == tenantId.Value);

        var events = await eventsQuery.ToListAsync(cancellationToken);

        // Get policy counts
        var policiesQuery = _context.Policies.AsQueryable();
        if (tenantId.HasValue)
            policiesQuery = policiesQuery.Where(p => p.TenantId == tenantId.Value);

        var totalPolicies = await policiesQuery.CountAsync(cancellationToken);
        var activePolicies = await policiesQuery.Where(p => p.IsActive).CountAsync(cancellationToken);
        var expiredPolicies = await policiesQuery.Where(p => p.ExpiresAt != null && p.ExpiresAt <= DateTime.UtcNow).CountAsync(cancellationToken);

        // Get temporary access counts
        var tempAccessQuery = _context.TemporaryAccessGrants.AsQueryable();
        if (tenantId.HasValue)
            tempAccessQuery = tempAccessQuery.Where(t => t.TenantId == tenantId.Value);

        var temporaryAccessGrants = await tempAccessQuery
            .Where(t => t.StartTime >= startDate && t.StartTime <= endDate)
            .CountAsync(cancellationToken);
        var activeTemporaryAccess = await tempAccessQuery
            .Where(t => t.Status == TemporaryAccessStatus.Active && t.EndTime > DateTime.UtcNow)
            .CountAsync(cancellationToken);

        var statistics = new ComplianceStatistics
        {
            TotalPolicies = totalPolicies,
            ActivePolicies = activePolicies,
            ExpiredPolicies = expiredPolicies,
            TotalEvaluations = events.Count(e => e.EventType == PolicyAuditEventType.AccessEvaluated),
            AccessGranted = events.Count(e => e.WasAllowed == true),
            AccessDenied = events.Count(e => e.WasAllowed == false),
            TemporaryAccessGrants = temporaryAccessGrants,
            ActiveTemporaryAccess = activeTemporaryAccess,
            FailedAccessAttempts = events.Count(e => e.WasAllowed == false && e.EventType == PolicyAuditEventType.AccessDenied),
            ComplianceViolations = events.Count(e => e.EventType == PolicyAuditEventType.ComplianceViolation)
        };

        // Top accessed resources
        statistics.TopAccessedResources = events
            .Where(e => !string.IsNullOrEmpty(e.Resource))
            .GroupBy(e => e.Resource!)
            .Select(g => new TopResource
            {
                Resource = g.Key,
                AccessCount = g.Count()
            })
            .OrderByDescending(r => r.AccessCount)
            .Take(10)
            .ToList();

        // Top active users
        statistics.TopActiveUsers = events
            .Where(e => e.UserId.HasValue)
            .GroupBy(e => e.UserId!.Value)
            .Select(g => new TopUser
            {
                UserId = g.Key,
                ActivityCount = g.Count()
            })
            .OrderByDescending(u => u.ActivityCount)
            .Take(10)
            .ToList();

        // Events by type
        statistics.EventsByType = events
            .GroupBy(e => e.EventType.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        // Events by day
        statistics.EventsByDay = events
            .GroupBy(e => e.CreatedAt.Date.ToString("yyyy-MM-dd"))
            .ToDictionary(g => g.Key, g => g.Count());

        return statistics;
    }

    public async Task<ComplianceReport> GenerateComplianceReportAsync(
        string framework,
        DateTime periodStart,
        DateTime periodEnd,
        Guid? tenantId,
        Guid generatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var statistics = await GetComplianceStatisticsAsync(periodStart, periodEnd, tenantId, cancellationToken);

        // Calculate compliance score (simple heuristic)
        var score = 100;
        if (statistics.ComplianceViolations > 0)
            score -= Math.Min(50, statistics.ComplianceViolations * 10);
        if (statistics.FailedAccessAttempts > statistics.AccessGranted * 0.1)
            score -= 20; // More than 10% failure rate
        if (statistics.ExpiredPolicies > 0)
            score -= Math.Min(10, statistics.ExpiredPolicies * 2);

        // Generate findings
        var findings = new List<ComplianceFinding>();

        if (statistics.ComplianceViolations > 0)
        {
            findings.Add(new ComplianceFinding
            {
                Severity = "High",
                Title = "Compliance Violations Detected",
                Description = $"{statistics.ComplianceViolations} compliance violations were detected during the reporting period",
                DetectedAt = DateTime.UtcNow,
                Status = "Open",
                Remediation = "Review audit logs and implement corrective controls"
            });
        }

        if (statistics.ExpiredPolicies > 0)
        {
            findings.Add(new ComplianceFinding
            {
                Severity = "Medium",
                Title = "Expired Policies",
                Description = $"{statistics.ExpiredPolicies} policies have expired and need review",
                DetectedAt = DateTime.UtcNow,
                Status = "Open",
                Remediation = "Review and update or deactivate expired policies"
            });
        }

        if (statistics.FailedAccessAttempts > statistics.AccessGranted * 0.1)
        {
            findings.Add(new ComplianceFinding
            {
                Severity = "Medium",
                Title = "High Failed Access Rate",
                Description = $"Failed access attempts ({statistics.FailedAccessAttempts}) exceed 10% of successful accesses",
                DetectedAt = DateTime.UtcNow,
                Status = "Open",
                Remediation = "Investigate potential unauthorized access attempts or misconfigured policies"
            });
        }

        var report = new ComplianceReport
        {
            Name = $"{framework} Compliance Report - {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}",
            Framework = framework,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            TenantId = tenantId,
            Status = ComplianceReportStatus.Completed,
            Score = Math.Max(0, score),
            Findings = JsonSerializer.Serialize(findings),
            Statistics = JsonSerializer.Serialize(statistics),
            Summary = $"Compliance report for {framework} covering period from {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}. " +
                     $"Total evaluations: {statistics.TotalEvaluations}, Access granted: {statistics.AccessGranted}, Access denied: {statistics.AccessDenied}. " +
                     $"Compliance score: {score}/100.",
            GeneratedByUserId = generatedByUserId,
            CompletedAt = DateTime.UtcNow
        };

        _context.ComplianceReports.Add(report);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Compliance report generated: {Framework} for period {Start} to {End}, Score: {Score}/100, Events: {Count}",
            framework, periodStart, periodEnd, score, statistics.TotalEvaluations);

        return report;
    }

    public async Task<List<PolicyAuditEvent>> DetectAnomaliesAsync(
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        // Comprehensive anomaly detection
        var events = await _context.PolicyAuditEvents
            .Where(e => e.CreatedAt >= since)
            .ToListAsync(cancellationToken);

        var anomalies = new List<PolicyAuditEvent>();

        // 1. Multiple failed access attempts by same user (brute force detection)
        var failedAttemptsByUser = events
            .Where(e => e.WasAllowed == false && e.UserId.HasValue)
            .GroupBy(e => e.UserId)
            .Where(g => g.Count() >= 5) // 5+ failed attempts
            .SelectMany(g => g)
            .ToList();

        foreach (var anomaly in failedAttemptsByUser)
        {
            anomaly.RiskScore = 80;
            anomaly.TriggeredAlert = true;
            anomaly.AlertDetails = "Multiple failed access attempts detected";
        }
        anomalies.AddRange(failedAttemptsByUser);

        // 2. Access attempts outside normal business hours (00:00-06:00, 22:00-23:59)
        var afterHoursAccess = events
            .Where(e => e.CreatedAt.Hour < 6 || e.CreatedAt.Hour >= 22)
            .Where(e => e.EventType == PolicyAuditEventType.AccessGranted)
            .ToList();

        foreach (var anomaly in afterHoursAccess)
        {
            anomaly.RiskScore = 40;
            anomaly.TriggeredAlert = true;
            anomaly.AlertDetails = "After-hours access detected";
        }
        anomalies.AddRange(afterHoursAccess);

        // 3. High-privilege access to sensitive resources
        var sensitiveResources = new[] { "admin", "database", "billing", "secrets", "keys" };
        var sensitiveAccess = events
            .Where(e => e.Resource != null && sensitiveResources.Any(sr => e.Resource.Contains(sr, StringComparison.OrdinalIgnoreCase)))
            .Where(e => e.WasAllowed == true)
            .ToList();

        foreach (var anomaly in sensitiveAccess)
        {
            anomaly.RiskScore = 60;
            anomaly.TriggeredAlert = true;
            anomaly.AlertDetails = "Access to sensitive resource";
        }
        anomalies.AddRange(sensitiveAccess);

        // 4. Rapid succession of access attempts from same user (potential automation)
        var rapidAccess = events
            .Where(e => e.UserId.HasValue)
            .GroupBy(e => e.UserId)
            .Where(g => g.Count() >= 20) // 20+ requests in the time window
            .SelectMany(g => g)
            .ToList();

        foreach (var anomaly in rapidAccess)
        {
            anomaly.RiskScore = 50;
            anomaly.TriggeredAlert = true;
            anomaly.AlertDetails = "Rapid access pattern detected (potential automation)";
        }
        anomalies.AddRange(rapidAccess);

        // Update anomalies in database
        if (anomalies.Any())
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        _logger.LogWarning(
            "Anomaly detection found {Count} suspicious events since {Since}",
            anomalies.Distinct().Count(), since);

        return anomalies.Distinct().ToList();
    }

    public async Task<int> CleanupOldAuditEventsAsync(
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

        var oldEvents = await _context.PolicyAuditEvents
            .Where(e => e.CreatedAt < cutoffDate)
            .ToListAsync(cancellationToken);

        _context.PolicyAuditEvents.RemoveRange(oldEvents);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Cleaned up {Count} audit events older than {Days} days",
            oldEvents.Count, retentionDays);

        return oldEvents.Count;
    }
}
