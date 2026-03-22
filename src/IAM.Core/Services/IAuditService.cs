using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for audit logging and compliance tracking
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Log a policy audit event
    /// </summary>
    Task LogPolicyEventAsync(
        PolicyAuditEventType eventType,
        Guid? policyId = null,
        Guid? userId = null,
        Guid? tenantId = null,
        string? resource = null,
        string? action = null,
        bool? wasAllowed = null,
        string? reason = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a policy evaluation result
    /// </summary>
    Task LogPolicyEvaluationAsync(
        PolicyEvaluationResult result,
        Guid userId,
        Guid tenantId,
        string resource,
        string action,
        string? ipAddress = null,
        string? deviceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit events with filtering
    /// </summary>
    Task<List<PolicyAuditEvent>> GetAuditEventsAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        Guid? userId = null,
        Guid? tenantId = null,
        Guid? policyId = null,
        PolicyAuditEventType? eventType = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get compliance statistics for a time period
    /// </summary>
    Task<ComplianceStatistics> GetComplianceStatisticsAsync(
        DateTime startDate,
        DateTime endDate,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate compliance report
    /// </summary>
    Task<ComplianceReport> GenerateComplianceReportAsync(
        string framework,
        DateTime periodStart,
        DateTime periodEnd,
        Guid? tenantId,
        Guid generatedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detect anomalous access patterns (background job)
    /// </summary>
    Task<List<PolicyAuditEvent>> DetectAnomaliesAsync(
        DateTime since,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clean up old audit events (retention policy)
    /// </summary>
    Task<int> CleanupOldAuditEventsAsync(
        int retentionDays,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Compliance statistics aggregate
/// </summary>
public class ComplianceStatistics
{
    public int TotalPolicies { get; set; }
    public int ActivePolicies { get; set; }
    public int ExpiredPolicies { get; set; }
    public int TotalEvaluations { get; set; }
    public int AccessGranted { get; set; }
    public int AccessDenied { get; set; }
    public int TemporaryAccessGrants { get; set; }
    public int ActiveTemporaryAccess { get; set; }
    public int ComplianceViolations { get; set; }
    public int FailedAccessAttempts { get; set; }
    public List<TopResource> TopAccessedResources { get; set; } = new();
    public List<TopUser> TopActiveUsers { get; set; } = new();
    public Dictionary<string, int> EventsByType { get; set; } = new();
    public Dictionary<string, int> EventsByDay { get; set; } = new();
}

public class TopResource
{
    public string Resource { get; set; } = string.Empty;
    public int AccessCount { get; set; }
}

public class TopUser
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int ActivityCount { get; set; }
}
