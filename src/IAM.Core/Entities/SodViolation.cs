namespace IAM.Core.Entities;

/// <summary>
/// Records a detected Segregation of Duties violation when a user holds conflicting roles.
/// </summary>
public class SodViolation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The SoD constraint that was violated.
    /// </summary>
    public Guid ConstraintId { get; set; }
    public SodConstraint Constraint { get; set; } = null!;

    /// <summary>
    /// The user who holds both conflicting roles.
    /// </summary>
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// First conflicting role held by the user.
    /// </summary>
    public Guid RoleA { get; set; }

    /// <summary>
    /// Second conflicting role held by the user.
    /// </summary>
    public Guid RoleB { get; set; }

    /// <summary>
    /// When the violation was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// How the violation was resolved (null if unresolved).
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// When the violation was resolved (null if unresolved).
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// User who resolved the violation.
    /// </summary>
    public Guid? ResolvedByUserId { get; set; }
}
