namespace IAM.Core.Entities;

/// <summary>
/// Assigns a role to an entire group within a tenant context.
/// All active members of the group inherit the role's permissions.
/// </summary>
public class GroupRole
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupId { get; set; }
    public Group? Group { get; set; }

    public Guid RoleId { get; set; }
    public Role? Role { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who assigned this role to the group
    /// </summary>
    public Guid? AssignedByUserId { get; set; }
}
