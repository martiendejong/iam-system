namespace IAM.Core.Entities;

/// <summary>
/// Represents a deployment region in the multi-region HA setup.
/// Tracks region health, latency, and primary/standby status.
/// </summary>
public class RegionConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable region name (e.g., "eu-west-1", "us-east-1")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Base URL endpoint for this region's API (e.g., "https://eu-west-1.iam.example.com")
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Whether this region is the primary (active) region
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Current operational status of the region
    /// </summary>
    public RegionStatus Status { get; set; } = RegionStatus.Active;

    /// <summary>
    /// Last successful health check timestamp
    /// </summary>
    public DateTime? LastHealthCheck { get; set; }

    /// <summary>
    /// Current latency in milliseconds to this region
    /// </summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Optional description or notes about this region
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Region priority for failover ordering (lower = higher priority)
    /// </summary>
    public int Priority { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsHealthy => Status == RegionStatus.Active
        && LastHealthCheck.HasValue
        && LastHealthCheck.Value > DateTime.UtcNow.AddMinutes(-5);
}

/// <summary>
/// Operational status of a deployment region
/// </summary>
public enum RegionStatus
{
    Active = 0,
    Standby = 1,
    Degraded = 2,
    Offline = 3
}
