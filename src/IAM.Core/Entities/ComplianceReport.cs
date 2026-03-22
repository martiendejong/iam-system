using System.Text.Json;

namespace IAM.Core.Entities;

/// <summary>
/// Compliance report for regulatory and security audits
/// Aggregates audit data for compliance frameworks (SOC2, GDPR, HIPAA, etc.)
/// </summary>
public class ComplianceReport
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Report name/title
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Compliance framework (SOC2, GDPR, HIPAA, ISO27001, Custom)
    /// </summary>
    public string Framework { get; set; } = "Custom";

    /// <summary>
    /// Tenant this report covers (null = system-wide)
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Report period start date
    /// </summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>
    /// Report period end date
    /// </summary>
    public DateTime PeriodEnd { get; set; }

    /// <summary>
    /// Report status
    /// </summary>
    public ComplianceReportStatus Status { get; set; } = ComplianceReportStatus.InProgress;

    /// <summary>
    /// Compliance score (0-100)
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// Findings in JSON format
    /// </summary>
    public string Findings { get; set; } = "[]";

    /// <summary>
    /// Recommendations for improvement in JSON format
    /// </summary>
    public string? Recommendations { get; set; }

    /// <summary>
    /// Report summary
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Statistics in JSON format
    /// Example: { "totalPolicies": 50, "activePolicies": 45, "violations": 3 }
    /// </summary>
    public string Statistics { get; set; } = "{}";

    /// <summary>
    /// User who generated this report
    /// </summary>
    public Guid GeneratedByUserId { get; set; }

    /// <summary>
    /// Report generation time
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Report completion time
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// File path if report was exported
    /// </summary>
    public string? ExportedFilePath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Parse findings from JSON
    /// </summary>
    public List<ComplianceFinding> GetFindings()
    {
        if (string.IsNullOrWhiteSpace(Findings))
            return new List<ComplianceFinding>();

        return JsonSerializer.Deserialize<List<ComplianceFinding>>(Findings) ?? new List<ComplianceFinding>();
    }

    /// <summary>
    /// Parse statistics from JSON
    /// </summary>
    public Dictionary<string, object> GetStatistics()
    {
        if (string.IsNullOrWhiteSpace(Statistics))
            return new Dictionary<string, object>();

        return JsonSerializer.Deserialize<Dictionary<string, object>>(Statistics) ?? new Dictionary<string, object>();
    }
}

/// <summary>
/// Individual compliance finding
/// </summary>
public class ComplianceFinding
{
    /// <summary>
    /// Finding severity
    /// </summary>
    public string Severity { get; set; } = "Info"; // Critical, High, Medium, Low, Info

    /// <summary>
    /// Compliance requirement ID (from framework)
    /// </summary>
    public string? RequirementId { get; set; }

    /// <summary>
    /// Finding title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Affected resources (policies, users, tenants)
    /// </summary>
    public List<string> AffectedResources { get; set; } = new();

    /// <summary>
    /// Recommended remediation action
    /// </summary>
    public string? Remediation { get; set; }

    /// <summary>
    /// Finding status
    /// </summary>
    public string Status { get; set; } = "Open"; // Open, InProgress, Resolved, Accepted

    /// <summary>
    /// When this finding was detected
    /// </summary>
    public DateTime DetectedAt { get; set; }
}

/// <summary>
/// Compliance report status
/// </summary>
public enum ComplianceReportStatus
{
    /// <summary>
    /// Report is being generated
    /// </summary>
    InProgress = 0,

    /// <summary>
    /// Report generation completed
    /// </summary>
    Completed = 1,

    /// <summary>
    /// Report generation failed
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Report was reviewed by auditor
    /// </summary>
    Reviewed = 3,

    /// <summary>
    /// Report was approved
    /// </summary>
    Approved = 4
}
