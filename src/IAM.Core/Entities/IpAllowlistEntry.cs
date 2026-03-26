namespace IAM.Core.Entities;

/// <summary>
/// Represents an IP allowlist entry for a tenant.
/// Uses CIDR notation to support single IPs and ranges.
/// </summary>
public class IpAllowlistEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// CIDR notation (e.g., "192.168.1.0/24" or "10.0.0.1/32" for single IP)
    /// </summary>
    public string Cidr { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description (e.g., "Office network", "VPN exit node")
    /// </summary>
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Tenant Tenant { get; set; } = null!;
}
