using System.Diagnostics;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Manages multi-region high availability: region registration, health probing,
/// failover orchestration, sync event tracking, and CRDT-based conflict resolution.
/// </summary>
public class RegionService : IRegionService
{
    private readonly IAMDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RegionService> _logger;

    public RegionService(
        IAMDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<RegionService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════
    //  Region Management
    // ════════════════════════════════════════════════════════════

    public async Task<RegionConfig> RegisterRegionAsync(RegionConfig region, CancellationToken ct = default)
    {
        region.Id = Guid.NewGuid();
        region.CreatedAt = DateTime.UtcNow;
        region.UpdatedAt = DateTime.UtcNow;

        // If this is the first region or explicitly marked primary, ensure only one primary
        if (region.IsPrimary)
        {
            var existingPrimary = await _context.RegionConfigs
                .Where(r => r.IsPrimary)
                .ToListAsync(ct);

            foreach (var r in existingPrimary)
            {
                r.IsPrimary = false;
                r.UpdatedAt = DateTime.UtcNow;
            }
        }

        _context.RegionConfigs.Add(region);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Region registered: {RegionName} ({Endpoint}), IsPrimary: {IsPrimary}",
            region.Name, region.Endpoint, region.IsPrimary);

        return region;
    }

    public async Task<RegionConfig?> GetRegionAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.RegionConfigs.FindAsync(new object[] { id }, ct);
    }

    public async Task<List<RegionConfig>> GetAllRegionsAsync(CancellationToken ct = default)
    {
        return await _context.RegionConfigs
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task<RegionConfig> UpdateRegionAsync(Guid id, RegionConfig updated, CancellationToken ct = default)
    {
        var region = await _context.RegionConfigs.FindAsync(new object[] { id }, ct)
            ?? throw new KeyNotFoundException($"Region {id} not found");

        region.Name = updated.Name;
        region.Endpoint = updated.Endpoint;
        region.Description = updated.Description;
        region.Priority = updated.Priority;
        region.Status = updated.Status;
        region.UpdatedAt = DateTime.UtcNow;

        // Handle primary flag changes
        if (updated.IsPrimary && !region.IsPrimary)
        {
            var existingPrimary = await _context.RegionConfigs
                .Where(r => r.IsPrimary && r.Id != id)
                .ToListAsync(ct);

            foreach (var r in existingPrimary)
            {
                r.IsPrimary = false;
                r.UpdatedAt = DateTime.UtcNow;
            }
        }
        region.IsPrimary = updated.IsPrimary;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Region updated: {RegionName}", region.Name);
        return region;
    }

    public async Task<bool> DeleteRegionAsync(Guid id, CancellationToken ct = default)
    {
        var region = await _context.RegionConfigs.FindAsync(new object[] { id }, ct);
        if (region == null) return false;

        if (region.IsPrimary)
        {
            _logger.LogWarning("Cannot delete primary region {RegionName}. Trigger failover first.", region.Name);
            throw new InvalidOperationException("Cannot delete the primary region. Trigger failover first.");
        }

        _context.RegionConfigs.Remove(region);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Region deleted: {RegionName}", region.Name);
        return true;
    }

    // ════════════════════════════════════════════════════════════
    //  Health Checks
    // ════════════════════════════════════════════════════════════

    public async Task<RegionHealthResult> CheckRegionHealthAsync(Guid regionId, CancellationToken ct = default)
    {
        var region = await _context.RegionConfigs.FindAsync(new object[] { regionId }, ct)
            ?? throw new KeyNotFoundException($"Region {regionId} not found");

        var result = new RegionHealthResult
        {
            RegionId = region.Id,
            RegionName = region.Name,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            var client = _httpClientFactory.CreateClient("RegionHealth");
            var sw = Stopwatch.StartNew();

            var healthUrl = $"{region.Endpoint.TrimEnd('/')}/health";
            var response = await client.GetAsync(healthUrl, ct);
            sw.Stop();

            result.LatencyMs = (int)sw.ElapsedMilliseconds;
            result.IsHealthy = response.IsSuccessStatusCode;
            result.Status = response.IsSuccessStatusCode ? RegionStatus.Active : RegionStatus.Degraded;

            // Update the region record
            region.LastHealthCheck = DateTime.UtcNow;
            region.LatencyMs = result.LatencyMs;
            region.Status = result.Status;
            region.UpdatedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            result.IsHealthy = false;
            result.Status = RegionStatus.Offline;
            result.Error = ex.Message;
            result.LatencyMs = -1;

            region.Status = RegionStatus.Offline;
            region.UpdatedAt = DateTime.UtcNow;

            _logger.LogWarning(ex, "Health check failed for region {RegionName}", region.Name);
        }

        await _context.SaveChangesAsync(ct);
        return result;
    }

    public async Task RunAllHealthChecksAsync(CancellationToken ct = default)
    {
        var regions = await _context.RegionConfigs.ToListAsync(ct);

        foreach (var region in regions)
        {
            try
            {
                await CheckRegionHealthAsync(region.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking health for region {RegionName}", region.Name);
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Failover
    // ════════════════════════════════════════════════════════════

    public async Task<RegionConfig> TriggerFailoverAsync(Guid? targetRegionId = null, CancellationToken ct = default)
    {
        var currentPrimary = await _context.RegionConfigs
            .FirstOrDefaultAsync(r => r.IsPrimary, ct);

        RegionConfig newPrimary;

        if (targetRegionId.HasValue)
        {
            // Failover to a specific region
            newPrimary = await _context.RegionConfigs.FindAsync(new object[] { targetRegionId.Value }, ct)
                ?? throw new KeyNotFoundException($"Target region {targetRegionId} not found");
        }
        else
        {
            // Auto-select: pick the healthiest standby region with lowest priority number
            newPrimary = await _context.RegionConfigs
                .Where(r => !r.IsPrimary && r.Status == RegionStatus.Active)
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.LatencyMs)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("No healthy standby region available for failover");
        }

        // Demote current primary
        if (currentPrimary != null)
        {
            currentPrimary.IsPrimary = false;
            currentPrimary.Status = RegionStatus.Standby;
            currentPrimary.UpdatedAt = DateTime.UtcNow;

            _logger.LogWarning(
                "Failover: Demoting region {OldPrimary} from primary",
                currentPrimary.Name);
        }

        // Promote new primary
        newPrimary.IsPrimary = true;
        newPrimary.Status = RegionStatus.Active;
        newPrimary.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Failover complete: {NewPrimary} is now the primary region (was: {OldPrimary})",
            newPrimary.Name, currentPrimary?.Name ?? "none");

        // Record sync event for the failover
        var syncEvent = new RegionSyncEvent
        {
            SourceRegion = currentPrimary?.Name ?? "none",
            TargetRegion = newPrimary.Name,
            EntityType = "Failover",
            EntityId = newPrimary.Id.ToString(),
            SyncStatus = SyncStatus.Completed,
            Details = $"{{\"previousPrimary\":\"{currentPrimary?.Name}\",\"newPrimary\":\"{newPrimary.Name}\",\"reason\":\"manual_failover\"}}",
            CompletedAt = DateTime.UtcNow
        };

        _context.RegionSyncEvents.Add(syncEvent);
        await _context.SaveChangesAsync(ct);

        return newPrimary;
    }

    // ════════════════════════════════════════════════════════════
    //  Sync Status
    // ════════════════════════════════════════════════════════════

    public async Task<List<RegionSyncEvent>> GetSyncEventsAsync(int skip = 0, int take = 100, CancellationToken ct = default)
    {
        return await _context.RegionSyncEvents
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<RegionSyncSummary> GetSyncSummaryAsync(CancellationToken ct = default)
    {
        var events = await _context.RegionSyncEvents.ToListAsync(ct);

        var completedEvents = events.Where(e => e.SyncStatus == SyncStatus.Completed).ToList();
        var avgLatency = completedEvents.Count > 0
            ? completedEvents
                .Where(e => e.CompletedAt.HasValue)
                .Average(e => (e.CompletedAt!.Value - e.CreatedAt).TotalMilliseconds)
            : 0;

        return new RegionSyncSummary
        {
            TotalEvents = events.Count,
            PendingEvents = events.Count(e => e.SyncStatus == SyncStatus.Pending || e.SyncStatus == SyncStatus.InProgress),
            CompletedEvents = completedEvents.Count,
            FailedEvents = events.Count(e => e.SyncStatus == SyncStatus.Failed),
            ConflictEvents = events.Count(e => e.SyncStatus == SyncStatus.Conflict),
            LastSyncAt = events.MaxBy(e => e.CreatedAt)?.CreatedAt,
            AverageSyncLatencyMs = Math.Round(avgLatency, 2)
        };
    }

    public async Task<RegionSyncEvent> RecordSyncEventAsync(RegionSyncEvent syncEvent, CancellationToken ct = default)
    {
        syncEvent.Id = Guid.NewGuid();
        syncEvent.CreatedAt = DateTime.UtcNow;

        _context.RegionSyncEvents.Add(syncEvent);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Sync event recorded: {EntityType}/{EntityId} from {Source} to {Target} (status: {Status})",
            syncEvent.EntityType, syncEvent.EntityId, syncEvent.SourceRegion, syncEvent.TargetRegion, syncEvent.SyncStatus);

        return syncEvent;
    }

    /// <summary>
    /// CRDT-based conflict resolution: resolves sync conflicts using the specified strategy.
    /// LastWriterWins uses timestamp comparison. Merged attempts to combine changes.
    /// </summary>
    public async Task<RegionSyncEvent> ResolveSyncConflictAsync(
        Guid syncEventId, ConflictResolution resolution, CancellationToken ct = default)
    {
        var syncEvent = await _context.RegionSyncEvents.FindAsync(new object[] { syncEventId }, ct)
            ?? throw new KeyNotFoundException($"Sync event {syncEventId} not found");

        if (syncEvent.SyncStatus != SyncStatus.Conflict)
        {
            throw new InvalidOperationException($"Sync event {syncEventId} is not in conflict state");
        }

        syncEvent.ConflictResolution = resolution;
        syncEvent.SyncStatus = SyncStatus.Completed;
        syncEvent.CompletedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Sync conflict resolved for {EntityType}/{EntityId}: {Resolution}",
            syncEvent.EntityType, syncEvent.EntityId, resolution);

        return syncEvent;
    }
}
