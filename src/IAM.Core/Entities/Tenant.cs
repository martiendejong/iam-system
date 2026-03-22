namespace IAM.Core.Entities;

/// <summary>
/// Represents a tenant in the multi-tenant system.
/// Hierarchical: Building → Floor → Room → Device
/// </summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Tenant type: Building, Floor, Room, Organization
    /// </summary>
    public string Type { get; set; } = "Organization";

    /// <summary>
    /// Parent tenant for hierarchical relationships
    /// </summary>
    public Guid? ParentTenantId { get; set; }
    public Tenant? ParentTenant { get; set; }

    /// <summary>
    /// JSON metadata for descriptive data (address, floor number, room capacity, device model, etc.)
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>
    /// JSON settings for tenant-specific configuration (temperature thresholds, access hours, etc.)
    /// </summary>
    public string? Settings { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Tenant> ChildTenants { get; set; } = new List<Tenant>();
    public ICollection<Role> Roles { get; set; } = new List<Role>();
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
