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

    private static readonly HashSet<string> ValidAggregations = new(StringComparer.OrdinalIgnoreCase)
        { "avg", "min", "max", "sum", "count" };

    public async Task<List<TelemetryAggregation>> AggregateAsync(TelemetryAggregationQuery query, CancellationToken ct = default)
    {
        var aggregation = query.Aggregation.ToLowerInvariant();
        if (!ValidAggregations.Contains(aggregation))
            throw new ArgumentException($"Unsupported aggregation '{query.Aggregation}'", nameof(query));

        // Bucketing/aggregation is pushed down to Postgres via date_bin() rather than loading
        // raw rows and grouping in C# - the previous approach materialized every matching row
        // into app memory per request, which does not scale to a 100-device/10K-point dashboard
        // query. date_bin is available on Postgres 14+; aggregation/groupBy values come from a
        // fixed allow-list (validated above and in the controller), never interpolated from
        // free-form user input, so building the aggregate/group-by SQL fragments below is safe.
        // Cast every branch to double precision so the reader can always read column 2 as a double,
        // regardless of which aggregation (e.g. COUNT returns bigint) produced it.
        var aggregateExpr = aggregation switch
        {
            "min" => "MIN(\"NumericValue\")::double precision",
            "max" => "MAX(\"NumericValue\")::double precision",
            "sum" => "SUM(\"NumericValue\")::double precision",
            "count" => "COUNT(\"NumericValue\")::double precision",
            _ => "AVG(\"NumericValue\")::double precision"
        };

        var groupByColumn = query.GroupBy?.ToLowerInvariant() switch
        {
            "device_id" => "\"DeviceId\"",
            "device_type" => "\"DeviceType\"",
            _ => null
        };
        var groupKeySelect = groupByColumn is null ? "NULL" : groupByColumn;

        var intervalSeconds = ParseIntervalToSeconds(query.Interval);
        var intervalLiteral = $"{intervalSeconds} seconds";

        var sql = $@"
            SELECT
                date_bin(@interval::interval, ""Timestamp"", @origin) AS bucket_start,
                {groupKeySelect} AS group_key,
                {aggregateExpr} AS value,
                COUNT(""NumericValue"") AS count
            FROM ""TelemetryRecords""
            WHERE ""MetricName"" = @metricName
              AND ""NumericValue"" IS NOT NULL
              AND ""Timestamp"" >= @startTime
              AND ""Timestamp"" <= @endTime
              AND (@deviceId IS NULL OR ""DeviceId"" = @deviceId)
              AND (@tenantId IS NULL OR ""TenantId"" = @tenantId)
            GROUP BY bucket_start, group_key
            ORDER BY bucket_start, group_key";

        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);

        try
        {
            // "Timestamp" is stored as timestamptz; Npgsql requires Kind=Utc for that mapping,
            // and query-string-bound DateTimes commonly arrive as Kind=Unspecified.
            var startTimeUtc = DateTime.SpecifyKind(query.StartTime, DateTimeKind.Utc);
            var endTimeUtc = DateTime.SpecifyKind(query.EndTime, DateTimeKind.Utc);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@interval", intervalLiteral);
            AddParameter(command, "@origin", startTimeUtc);
            AddParameter(command, "@metricName", query.MetricName);
            AddParameter(command, "@startTime", startTimeUtc);
            AddParameter(command, "@endTime", endTimeUtc);
            AddParameter(command, "@deviceId", (object?)query.DeviceId ?? DBNull.Value);
            AddParameter(command, "@tenantId", (object?)query.TenantId ?? DBNull.Value);

            var results = new List<TelemetryAggregation>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var bucketStart = reader.GetDateTime(0);
                results.Add(new TelemetryAggregation
                {
                    BucketStart = bucketStart,
                    BucketEnd = bucketStart.AddSeconds(intervalSeconds),
                    GroupKey = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Value = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                    Count = (int)reader.GetInt64(3)
                });
            }

            return results;
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
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

    public async Task<List<TelemetryRecord>> GetLatestAsync(string deviceId, CancellationToken ct = default)
    {
        // EF Core cannot translate "OrderBy().First() per group" into SQL for arbitrary columns.
        // Postgres' DISTINCT ON is the standard, index-friendly way to get one latest row per
        // metric in a single query (uses the existing DeviceId+MetricName+Timestamp index).
        return await _context.TelemetryRecords
            .FromSqlInterpolated($@"
                SELECT DISTINCT ON (""MetricName"") *
                FROM ""TelemetryRecords""
                WHERE ""DeviceId"" = {deviceId}
                ORDER BY ""MetricName"", ""Timestamp"" DESC")
            .OrderBy(r => r.MetricName)
            .ToListAsync(ct);
    }

    public async Task<TelemetryRecord?> GetLatestAsync(string deviceId, string metricName, CancellationToken ct = default)
    {
        return await _context.TelemetryRecords
            .Where(r => r.DeviceId == deviceId && r.MetricName == metricName)
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefaultAsync(ct);
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
