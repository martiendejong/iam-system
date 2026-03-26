namespace IAM.Core.Entities;

/// <summary>
/// Audit log entry for blocked IP addresses.
/// Records every time an IP is denied access due to network policies.
/// </summary>
public class BlockedIpLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The blocked IP address
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Reason for blocking (e.g., "Not in IP allowlist", "Blocked country: CN", "Outside geofence")
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Resolved country code (ISO 3166-1 alpha-2) from geo lookup, if available
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// Resolved city from geo lookup, if available
    /// </summary>
    public string? City { get; set; }

    public DateTime BlockedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Tenant? Tenant { get; set; }
}
