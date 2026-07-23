using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

/// <summary>
/// Represents a user group for team management.
/// Supports hierarchical groups (parent/child), role assignments, and membership with expiration.
/// </summary>
public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid? ParentGroupId { get; set; }
    public Group? ParentGroup { get; set; }

    /// <summary>
    /// Group type: team, department, project, custom
    /// </summary>
    [MaxLength(50)]
    public string GroupType { get; set; } = "team";

    /// <summary>
    /// JSON metadata for group-specific data
    /// </summary>
    public string? Metadata { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<GroupMembership> Members { get; set; } = new List<GroupMembership>();
    public ICollection<GroupRole> GroupRoles { get; set; } = new List<GroupRole>();
    public ICollection<Group> ChildGroups { get; set; } = new List<Group>();
}
