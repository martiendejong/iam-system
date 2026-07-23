using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing multi-region high availability:
/// region registration, health checks, synchronization status,
/// failover orchestration, and CRDT-based conflict resolution.
/// </summary>
public interface IRegionService
{
    // ── Region Management ────────────────────────────────────────
    Task<RegionConfig> RegisterRegionAsync(RegionConfig region, CancellationToken ct = default);
    Task<RegionConfig?> GetRegionAsync(Guid id, CancellationToken ct = default);
    Task<List<RegionConfig>> GetAllRegionsAsync(CancellationToken ct = default);
    Task<RegionConfig> UpdateRegionAsync(Guid id, RegionConfig updated, CancellationToken ct = default);
    Task<bool> DeleteRegionAsync(Guid id, CancellationToken ct = default);

    // ── Health Checks ────────────────────────────────────────────
    Task<RegionHealthResult> CheckRegionHealthAsync(Guid regionId, CancellationToken ct = default);
    Task RunAllHealthChecksAsync(CancellationToken ct = default);

    // ── Failover ─────────────────────────────────────────────────
    Task<RegionConfig> TriggerFailoverAsync(Guid? targetRegionId = null, CancellationToken ct = default);

    // ── Sync Status ──────────────────────────────────────────────
    Task<List<RegionSyncEvent>> GetSyncEventsAsync(int skip = 0, int take = 100, CancellationToken ct = default);
    Task<RegionSyncSummary> GetSyncSummaryAsync(CancellationToken ct = default);
    Task<RegionSyncEvent> RecordSyncEventAsync(RegionSyncEvent syncEvent, CancellationToken ct = default);
    Task<RegionSyncEvent> ResolveSyncConflictAsync(Guid syncEventId, ConflictResolution resolution, CancellationToken ct = default);
}

/// <summary>
/// Result of a region health check
/// </summary>
public class RegionHealthResult
{
    public Guid RegionId { get; set; }
    public string RegionName { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public int LatencyMs { get; set; }
    public RegionStatus Status { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
}

/// <summary>
/// Summary of synchronization status across all regions
/// </summary>
public class RegionSyncSummary
{
    public int TotalEvents { get; set; }
    public int PendingEvents { get; set; }
    public int CompletedEvents { get; set; }
    public int FailedEvents { get; set; }
    public int ConflictEvents { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public double AverageSyncLatencyMs { get; set; }
}
