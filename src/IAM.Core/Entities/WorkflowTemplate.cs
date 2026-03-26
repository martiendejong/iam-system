namespace IAM.Core.Entities;

/// <summary>
/// Defines an approval workflow template that determines how access requests
/// for a specific resource type are routed through approval chains.
/// Templates are matched by tenant + resource type.
/// </summary>
public class WorkflowTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant this template belongs to. Null = global template.
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Display name for this workflow template.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this workflow handles.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Resource type this template applies to (e.g., "Role", "Building", "Device").
    /// Used to match incoming access requests to the correct workflow.
    /// </summary>
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    /// JSON array defining the approval steps.
    /// Format: [{"order": 1, "approverRoleId": "guid", "approverUserId": "guid", "quorumCount": 1}]
    /// Either approverRoleId or approverUserId should be set per step.
    /// </summary>
    public string Steps { get; set; } = "[]";

    /// <summary>
    /// Number of hours before an unacted-upon request automatically expires.
    /// Null means no auto-expiry.
    /// </summary>
    public int? AutoExpireHours { get; set; }

    /// <summary>
    /// JSON array defining auto-approve rules.
    /// Format: [{"resourceType": "Role", "roleId": "guid", "maxPriority": "Normal"}]
    /// Requests matching these rules are auto-approved without human intervention.
    /// </summary>
    public string? AutoApproveRules { get; set; }

    /// <summary>
    /// Whether this template is active and should be used for matching.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
