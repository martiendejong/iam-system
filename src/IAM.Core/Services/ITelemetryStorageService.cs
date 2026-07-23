using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface ITelemetryStorageService
{
    Task IngestAsync(TelemetryRecord record, CancellationToken ct = default);
    Task IngestBatchAsync(List<TelemetryRecord> records, CancellationToken ct = default);
    Task<TelemetryQueryResult> QueryAsync(TelemetryQuery query, CancellationToken ct = default);
    Task<List<TelemetryAggregation>> AggregateAsync(TelemetryAggregationQuery query, CancellationToken ct = default);
    Task<List<string>> GetMetricNamesAsync(string? deviceId = null, CancellationToken ct = default);
    Task<TelemetryStatistics> GetStatisticsAsync(Guid? tenantId = null, CancellationToken ct = default);
    Task<int> CleanupOldDataAsync(int retentionDays = 90, CancellationToken ct = default);
}

public class TelemetryQuery
{
    public string? DeviceId { get; set; }
    public string? MetricName { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int Limit { get; set; } = 1000;
    public string OrderBy { get; set; } = "timestamp_desc";
}

public class TelemetryQueryResult
{
    public List<TelemetryRecord> Records { get; set; } = new();
    public int TotalCount { get; set; }
    public DateTime? EarliestTimestamp { get; set; }
    public DateTime? LatestTimestamp { get; set; }
}

public class TelemetryAggregationQuery
{
    public string? DeviceId { get; set; }
    public string MetricName { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Aggregation { get; set; } = "avg"; // avg, min, max, sum, count
    public string Interval { get; set; } = "1h"; // 1m, 5m, 15m, 1h, 1d
    public string? GroupBy { get; set; } // device_id, device_type
}

public class TelemetryAggregation
{
    public DateTime BucketStart { get; set; }
    public DateTime BucketEnd { get; set; }
    public double Value { get; set; }
    public int Count { get; set; }
    public string? GroupKey { get; set; }
}

public class TelemetryStatistics
{
    public long TotalRecords { get; set; }
    public int UniqueDevices { get; set; }
    public int UniqueMetrics { get; set; }
    public long RecordsToday { get; set; }
    public DateTime? OldestRecord { get; set; }
    public DateTime? NewestRecord { get; set; }
}
