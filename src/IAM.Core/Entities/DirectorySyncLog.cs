namespace IAM.Core.Entities;

/// <summary>
/// Log entry for a directory sync operation. Tracks what happened during each sync run.
/// </summary>
public class DirectorySyncLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The sync configuration that triggered this sync
    /// </summary>
    public Guid ConfigId { get; set; }
    public DirectorySyncConfig? Config { get; set; }

    /// <summary>
    /// Type of sync operation
    /// </summary>
    public DirectorySyncType SyncType { get; set; } = DirectorySyncType.Full;

    /// <summary>
    /// Final status of this sync run
    /// </summary>
    public DirectorySyncStatus Status { get; set; } = DirectorySyncStatus.Running;

    /// <summary>
    /// Number of new users created during this sync
    /// </summary>
    public int UsersCreated { get; set; }

    /// <summary>
    /// Number of existing users updated during this sync
    /// </summary>
    public int UsersUpdated { get; set; }

    /// <summary>
    /// Number of users disabled during this sync (present in IAM but not in directory)
    /// </summary>
    public int UsersDisabled { get; set; }

    /// <summary>
    /// Number of groups synced during this sync
    /// </summary>
    public int GroupsSynced { get; set; }

    /// <summary>
    /// JSON array of error messages encountered during sync
    /// </summary>
    public string? Errors { get; set; }

    /// <summary>
    /// When this sync run started
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this sync run completed (null if still running)
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}

public enum DirectorySyncType
{
    Full,
    Delta
}

public enum DirectorySyncStatus
{
    Running,
    Success,
    Failed,
    PartialSuccess
}
