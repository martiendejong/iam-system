namespace IAM.Core.Entities;

public class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant this role belongs to. Null = global role
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Category { get; set; }

    /// <summary>
    /// System roles cannot be deleted
    /// </summary>
    public bool IsSystemRole { get; set; }

    /// <summary>
    /// JSON array of permission strings
    /// Example: ["Building.View", "Building.Manage", "HVAC.Control"]
    /// </summary>
    public string Permissions { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
