namespace IAM.Core.Entities;

/// <summary>
/// Tracks synchronization events between regions.
/// Used for replication auditing and conflict resolution tracking.
/// </summary>
public class RegionSyncEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Name of the source region that originated the change
    /// </summary>
    public string SourceRegion { get; set; } = string.Empty;

    /// <summary>
    /// Name of the target region that received the change
    /// </summary>
    public string TargetRegion { get; set; } = string.Empty;

    /// <summary>
    /// Type of entity being synchronized (e.g., "User", "Role", "Policy")
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// ID of the entity being synchronized
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    /// Current synchronization status
    /// </summary>
    public SyncStatus SyncStatus { get; set; } = SyncStatus.Pending;

    /// <summary>
    /// How the conflict was resolved (if any)
    /// </summary>
    public ConflictResolution ConflictResolution { get; set; } = ConflictResolution.None;

    /// <summary>
    /// JSON details about the sync event (payload hash, version vectors, etc.)
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Error message if sync failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Number of retry attempts
    /// </summary>
    public int RetryCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Status of a region synchronization event
/// </summary>
public enum SyncStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Failed = 3,
    Conflict = 4
}

/// <summary>
/// Strategy used to resolve synchronization conflicts
/// </summary>
public enum ConflictResolution
{
    None = 0,
    LastWriterWins = 1,
    SourceWins = 2,
    TargetWins = 3,
    Merged = 4,
    Manual = 5
}
