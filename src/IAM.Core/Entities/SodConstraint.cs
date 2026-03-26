namespace IAM.Core.Entities;

/// <summary>
/// Segregation of Duties (SoD) constraint defining conflicting role pairs.
/// When both roles are assigned to the same user, a violation is detected.
/// </summary>
public class SodConstraint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name for this constraint (e.g., "Finance Approver vs Finance Requester").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tenant context for this constraint.
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// First conflicting role.
    /// </summary>
    public Guid ConflictingRoleA { get; set; }
    public Role RoleA { get; set; } = null!;

    /// <summary>
    /// Second conflicting role.
    /// </summary>
    public Guid ConflictingRoleB { get; set; }
    public Role RoleB { get; set; } = null!;

    /// <summary>
    /// Description of why these roles conflict.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Severity of the constraint violation.
    /// Warning: logs the violation but allows the assignment.
    /// Block: prevents the role assignment entirely.
    /// </summary>
    public SodSeverity Severity { get; set; } = SodSeverity.Warning;

    /// <summary>
    /// Whether this constraint is currently enforced.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Severity level for SoD constraint violations.
/// </summary>
public enum SodSeverity
{
    /// <summary>
    /// Log the violation but allow the assignment.
    /// </summary>
    Warning = 0,

    /// <summary>
    /// Prevent the role assignment entirely.
    /// </summary>
    Block = 1
}
