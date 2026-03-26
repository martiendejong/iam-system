using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class TelemetryStorageService : ITelemetryStorageService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<TelemetryStorageService> _logger;

    public TelemetryStorageService(IAMDbContext context, ILogger<TelemetryStorageService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task IngestAsync(TelemetryRecord record, CancellationToken ct = default)
    {
        if (record.Id == Guid.Empty)
            record.Id = Guid.NewGuid();

        if (record.Timestamp == default)
            record.Timestamp = DateTime.UtcNow;

        record.CreatedAt = DateTime.UtcNow;

        _context.TelemetryRecords.Add(record);
        await _context.SaveChangesAsync(ct);
    }

    public async Task IngestBatchAsync(List<TelemetryRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return;

        var now = DateTime.UtcNow;

        foreach (var record in records)
        {
            if (record.Id == Guid.Empty)
                record.Id = Guid.NewGuid();

            if (record.Timestamp == default)
                record.Timestamp = now;

            record.CreatedAt = now;
        }

        _context.TelemetryRecords.AddRange(records);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Ingested {Count} telemetry records in batch", records.Count);
    }

    public async Task<TelemetryQueryResult> QueryAsync(TelemetryQuery query, CancellationToken ct = default)
    {
        var dbQuery = _context.TelemetryRecords.AsQueryable();

        // Apply filters
        if (!string.IsNullOrEmpty(query.DeviceId))
            dbQuery = dbQuery.Where(r => r.DeviceId == query.DeviceId);

        if (!string.IsNullOrEmpty(query.MetricName))
            dbQuery = dbQuery.Where(r => r.MetricName == query.MetricName);

        if (query.TenantId.HasValue)
            dbQuery = dbQuery.Where(r => r.TenantId == query.TenantId.Value);

        if (query.StartTime != default)
            dbQuery = dbQuery.Where(r => r.Timestamp >= query.StartTime);

        if (query.EndTime != default)
            dbQuery = dbQuery.Where(r => r.Timestamp <= query.EndTime);

        // Get total count before pagination
        var totalCount = await dbQuery.CountAsync(ct);

        // Apply ordering
        dbQuery = query.OrderBy?.ToLowerInvariant() switch
        {
            "timestamp_asc" => dbQuery.OrderBy(r => r.Timestamp),
            "metric_name" => dbQuery.OrderBy(r => r.MetricName).ThenByDescending(r => r.Timestamp),
            "device_id" => dbQuery.OrderBy(r => r.DeviceId).ThenByDescending(r => r.Timestamp),
            _ => dbQuery.OrderByDescending(r => r.Timestamp) // timestamp_desc is default
        };

        // Apply limit
        var limit = Math.Clamp(query.Limit, 1, 10000);
        var records = await dbQuery.Take(limit).ToListAsync(ct);

        return new TelemetryQueryResult
        {
            Records = records,
            TotalCount = totalCount,
            EarliestTimestamp = records.Count > 0 ? records.Min(r => r.Timestamp) : null,
            LatestTimestamp = records.Count > 0 ? records.Max(r => r.Timestamp) : null
        };
    }

    public async Task<List<TelemetryAggregation>> AggregateAsync(TelemetryAggregationQuery query, CancellationToken ct = default)
    {
        var dbQuery = _context.TelemetryRecords
            .Where(r => r.MetricName == query.MetricName)
            .Where(r => r.NumericValue.HasValue)
            .Where(r => r.Timestamp >= query.StartTime && r.Timestamp <= query.EndTime);

        if (!string.IsNullOrEmpty(query.DeviceId))
            dbQuery = dbQuery.Where(r => r.DeviceId == query.DeviceId);

        if (query.TenantId.HasValue)
            dbQuery = dbQuery.Where(r => r.TenantId == query.TenantId.Value);

        var intervalSeconds = ParseIntervalToSeconds(query.Interval);
        var startTicks = query.StartTime.Ticks;

        // Materialize the filtered data, then aggregate in memory.
        // EF Core with PostgreSQL cannot translate arbitrary date-bucketing expressions.
        var rawRecords = await dbQuery
            .Select(r => new
            {
                r.Timestamp,
                NumericValue = r.NumericValue!.Value,
                r.DeviceId,
                r.DeviceType
            })
            .ToListAsync(ct);

        // Group into time buckets + optional group key
        var grouped = rawRecords
            .GroupBy(r =>
            {
                var bucketIndex = (long)((r.Timestamp - query.StartTime).TotalSeconds / intervalSeconds);
                var bucketStart = query.StartTime.AddSeconds(bucketIndex * intervalSeconds);

                string? groupKey = query.GroupBy?.ToLowerInvariant() switch
                {
                    "device_id" => r.DeviceId,
                    "device_type" => r.DeviceType,
                    _ => null
                };

                return new { BucketStart = bucketStart, GroupKey = groupKey };
            });

        var results = new List<TelemetryAggregation>();

        foreach (var group in grouped.OrderBy(g => g.Key.BucketStart).ThenBy(g => g.Key.GroupKey))
        {
            var values = group.Select(r => r.NumericValue).ToList();
            var aggregatedValue = query.Aggregation.ToLowerInvariant() switch
            {
                "min" => values.Min(),
                "max" => values.Max(),
                "sum" => values.Sum(),
                "count" => values.Count,
                _ => values.Average() // "avg" is default
            };

            results.Add(new TelemetryAggregation
            {
                BucketStart = group.Key.BucketStart,
                BucketEnd = group.Key.BucketStart.AddSeconds(intervalSeconds),
                Value = aggregatedValue,
                Count = values.Count,
                GroupKey = group.Key.GroupKey
            });
        }

        return results;
    }

    public async Task<List<string>> GetMetricNamesAsync(string? deviceId = null, CancellationToken ct = default)
    {
        var query = _context.TelemetryRecords.AsQueryable();

        if (!string.IsNullOrEmpty(deviceId))
            query = query.Where(r => r.DeviceId == deviceId);

        return await query
            .Select(r => r.MetricName)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);
    }

    public async Task<TelemetryStatistics> GetStatisticsAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var query = _context.TelemetryRecords.AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(r => r.TenantId == tenantId.Value);

        var todayStart = DateTime.UtcNow.Date;

        var totalRecords = await query.LongCountAsync(ct);

        if (totalRecords == 0)
        {
            return new TelemetryStatistics
            {
                TotalRecords = 0,
                UniqueDevices = 0,
                UniqueMetrics = 0,
                RecordsToday = 0,
                OldestRecord = null,
                NewestRecord = null
            };
        }

        var uniqueDevices = await query.Select(r => r.DeviceId).Distinct().CountAsync(ct);
        var uniqueMetrics = await query.Select(r => r.MetricName).Distinct().CountAsync(ct);
        var recordsToday = await query.Where(r => r.Timestamp >= todayStart).LongCountAsync(ct);
        var oldestRecord = await query.MinAsync(r => r.Timestamp, ct);
        var newestRecord = await query.MaxAsync(r => r.Timestamp, ct);

        return new TelemetryStatistics
        {
            TotalRecords = totalRecords,
            UniqueDevices = uniqueDevices,
            UniqueMetrics = uniqueMetrics,
            RecordsToday = recordsToday,
            OldestRecord = oldestRecord,
            NewestRecord = newestRecord
        };
    }

    public async Task<int> CleanupOldDataAsync(int retentionDays = 90, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        var count = await _context.TelemetryRecords
            .Where(r => r.Timestamp < cutoff)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation(
            "Cleaned up {Count} telemetry records older than {RetentionDays} days (cutoff: {Cutoff:O})",
            count, retentionDays, cutoff);

        return count;
    }

    private static double ParseIntervalToSeconds(string interval)
    {
        if (string.IsNullOrEmpty(interval))
            return 3600; // Default 1 hour

        var value = interval[..^1];
        var unit = interval[^1];

        if (!double.TryParse(value, out var numericValue))
            return 3600;

        return unit switch
        {
            'm' => numericValue * 60,
            'h' => numericValue * 3600,
            'd' => numericValue * 86400,
            _ => 3600
        };
    }
}
