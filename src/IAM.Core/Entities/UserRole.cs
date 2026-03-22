namespace IAM.Core.Entities;

/// <summary>
/// Assigns a role to a user, optionally scoped to a tenant
/// </summary>
public class UserRole
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>
    /// Tenant context for this role assignment.
    /// Null = role applies globally
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// User who granted this role
    /// </summary>
    public Guid? GrantedBy { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Time-limited role (e.g., contractor access for 3 months)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
