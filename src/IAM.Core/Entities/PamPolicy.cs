namespace IAM.Core.Entities;

/// <summary>
/// PAM policy configuration per role/tenant.
/// Defines rules for privileged access checkout including duration limits,
/// approval requirements, and break-glass configuration.
/// </summary>
public class PamPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant this policy applies to
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Role this policy governs (the privileged role being checked out)
    /// </summary>
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>
    /// Maximum allowed session duration in minutes
    /// </summary>
    public int MaxDurationMinutes { get; set; } = 480; // 8 hours default

    /// <summary>
    /// Whether a justification is required for checkout
    /// </summary>
    public bool RequireJustification { get; set; } = true;

    /// <summary>
    /// Whether approval is required before checkout activates
    /// </summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>
    /// Role whose members can approve checkout requests (null = any admin)
    /// </summary>
    public Guid? ApproverRoleId { get; set; }
    public Role? ApproverRole { get; set; }

    /// <summary>
    /// Whether break-glass emergency access is enabled for this role
    /// </summary>
    public bool BreakGlassEnabled { get; set; } = false;

    /// <summary>
    /// Number of approvers required for break-glass access (4-eyes principle)
    /// </summary>
    public int BreakGlassApproversRequired { get; set; } = 2;

    /// <summary>
    /// Whether this policy is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
