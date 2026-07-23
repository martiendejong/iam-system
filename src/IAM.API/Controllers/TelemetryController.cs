using System.Globalization;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TelemetryController : ControllerBase
{
    private readonly ITelemetryStorageService _telemetryService;

    public TelemetryController(ITelemetryStorageService telemetryService)
    {
        _telemetryService = telemetryService;
    }

    /// <summary>
    /// Ingest a single telemetry record from a device.
    /// </summary>
    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromBody] IngestTelemetryRequest request, CancellationToken ct)
    {
        var record = new TelemetryRecord
        {
            Id = Guid.NewGuid(),
            DeviceId = request.DeviceId,
            MetricName = request.MetricName,
            NumericValue = request.NumericValue,
            StringValue = request.StringValue,
            JsonValue = request.JsonValue,
            Unit = request.Unit,
            TenantId = request.TenantId,
            DeviceType = request.DeviceType,
            Tags = request.Tags,
            Timestamp = request.Timestamp ?? DateTime.UtcNow
        };

        await _telemetryService.IngestAsync(record, ct);

        return Ok(new
        {
            id = record.Id,
            deviceId = record.DeviceId,
            metricName = record.MetricName,
            timestamp = record.Timestamp,
            message = "Telemetry record ingested successfully"
        });
    }

    /// <summary>
    /// Ingest a batch of telemetry records (up to 1000 per request).
    /// </summary>
    [HttpPost("ingest/batch")]
    public async Task<IActionResult> IngestBatch([FromBody] BatchIngestTelemetryRequest request, CancellationToken ct)
    {
        if (request.Records == null || request.Records.Count == 0)
        {
            return BadRequest(new { error = "No records provided" });
        }

        if (request.Records.Count > 1000)
        {
            return BadRequest(new { error = "Maximum 1000 records per batch. Received: " + request.Records.Count });
        }

        var records = request.Records.Select(r => new TelemetryRecord
        {
            Id = Guid.NewGuid(),
            DeviceId = r.DeviceId,
            MetricName = r.MetricName,
            NumericValue = r.NumericValue,
            StringValue = r.StringValue,
            JsonValue = r.JsonValue,
            Unit = r.Unit,
            TenantId = r.TenantId,
            DeviceType = r.DeviceType,
            Tags = r.Tags,
            Timestamp = r.Timestamp ?? DateTime.UtcNow
        }).ToList();

        await _telemetryService.IngestBatchAsync(records, ct);

        return Ok(new
        {
            count = records.Count,
            message = $"Successfully ingested {records.Count} telemetry records"
        });
    }

    /// <summary>
    /// Query telemetry records with filters.
    /// </summary>
    [HttpGet("query")]
    public async Task<IActionResult> Query(
        [FromQuery] string? deviceId,
        [FromQuery] string? metricName,
        [FromQuery] Guid? tenantId,
        [FromQuery] DateTime? startTime,
        [FromQuery] DateTime? endTime,
        [FromQuery] int limit = 1000,
        [FromQuery] string orderBy = "timestamp_desc",
        CancellationToken ct = default)
    {
        var query = new TelemetryQuery
        {
            DeviceId = deviceId,
            MetricName = metricName,
            TenantId = tenantId,
            StartTime = startTime ?? DateTime.UtcNow.AddHours(-24),
            EndTime = endTime ?? DateTime.UtcNow,
            Limit = limit,
            OrderBy = orderBy
        };

        var result = await _telemetryService.QueryAsync(query, ct);

        return Ok(new
        {
            records = result.Records.Select(r => new
            {
                id = r.Id,
                deviceId = r.DeviceId,
                metricName = r.MetricName,
                numericValue = r.NumericValue,
                stringValue = r.StringValue,
                jsonValue = r.JsonValue,
                unit = r.Unit,
                tenantId = r.TenantId,
                deviceType = r.DeviceType,
                tags = r.Tags,
                timestamp = r.Timestamp
            }),
            totalCount = result.TotalCount,
            earliestTimestamp = result.EarliestTimestamp,
            latestTimestamp = result.LatestTimestamp
        });
    }

    /// <summary>
    /// Get aggregated telemetry data (avg, min, max, sum, count) over time intervals.
    /// </summary>
    [HttpGet("aggregate")]
    public async Task<IActionResult> Aggregate(
        [FromQuery] string metricName,
        [FromQuery] string? deviceId,
        [FromQuery] Guid? tenantId,
        [FromQuery] string aggregation = "avg",
        [FromQuery] string interval = "1h",
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null,
        [FromQuery] string? groupBy = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(metricName))
        {
            return BadRequest(new { error = "metricName is required" });
        }

        var validAggregations = new[] { "avg", "min", "max", "sum", "count" };
        if (!validAggregations.Contains(aggregation.ToLowerInvariant()))
        {
            return BadRequest(new { error = $"Invalid aggregation. Must be one of: {string.Join(", ", validAggregations)}" });
        }

        var validIntervals = new[] { "1m", "5m", "15m", "1h", "1d" };
        if (!validIntervals.Contains(interval.ToLowerInvariant()))
        {
            return BadRequest(new { error = $"Invalid interval. Must be one of: {string.Join(", ", validIntervals)}" });
        }

        var query = new TelemetryAggregationQuery
        {
            DeviceId = deviceId,
            MetricName = metricName,
            TenantId = tenantId,
            StartTime = startTime ?? DateTime.UtcNow.AddHours(-24),
            EndTime = endTime ?? DateTime.UtcNow,
            Aggregation = aggregation,
            Interval = interval,
            GroupBy = groupBy
        };

        var result = await _telemetryService.AggregateAsync(query, ct);

        return Ok(new
        {
            metricName,
            aggregation,
            interval,
            buckets = result.Select(a => new
            {
                bucketStart = a.BucketStart,
                bucketEnd = a.BucketEnd,
                value = a.Value,
                count = a.Count,
                groupKey = a.GroupKey
            })
        });
    }

    /// <summary>
    /// List available metric names (optionally filtered by device).
    /// </summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetricNames([FromQuery] string? deviceId, CancellationToken ct)
    {
        var metrics = await _telemetryService.GetMetricNamesAsync(deviceId, ct);

        return Ok(new
        {
            deviceId,
            metrics,
            count = metrics.Count
        });
    }

    /// <summary>
    /// Get telemetry storage statistics.
    /// </summary>
    [HttpGet("statistics")]
    public async Task<IActionResult> GetStatistics([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var stats = await _telemetryService.GetStatisticsAsync(tenantId, ct);

        return Ok(new
        {
            totalRecords = stats.TotalRecords,
            uniqueDevices = stats.UniqueDevices,
            uniqueMetrics = stats.UniqueMetrics,
            recordsToday = stats.RecordsToday,
            oldestRecord = stats.OldestRecord,
            newestRecord = stats.NewestRecord
        });
    }

    /// <summary>
    /// Trigger cleanup of old telemetry data (admin only).
    /// </summary>
    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup([FromBody] CleanupRequest? request, CancellationToken ct)
    {
        var retentionDays = request?.RetentionDays ?? 90;

        if (retentionDays < 1)
        {
            return BadRequest(new { error = "Retention days must be at least 1" });
        }

        var deletedCount = await _telemetryService.CleanupOldDataAsync(retentionDays, ct);

        return Ok(new
        {
            deletedCount,
            retentionDays,
            cutoffDate = DateTime.UtcNow.AddDays(-retentionDays),
            message = $"Cleaned up {deletedCount} records older than {retentionDays} days"
        });
    }

    /// <summary>
    /// Export telemetry data as CSV.
    /// Returns CSV when Accept header contains text/csv, otherwise JSON.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? deviceId,
        [FromQuery] string? metricName,
        [FromQuery] Guid? tenantId,
        [FromQuery] DateTime? startTime,
        [FromQuery] DateTime? endTime,
        [FromQuery] int limit = 10000,
        CancellationToken ct = default)
    {
        var query = new TelemetryQuery
        {
            DeviceId = deviceId,
            MetricName = metricName,
            TenantId = tenantId,
            StartTime = startTime ?? DateTime.UtcNow.AddDays(-7),
            EndTime = endTime ?? DateTime.UtcNow,
            Limit = Math.Min(limit, 100000), // Hard cap at 100k for exports
            OrderBy = "timestamp_asc"
        };

        var result = await _telemetryService.QueryAsync(query, ct);

        // Check Accept header for CSV
        var acceptHeader = Request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = new StringBuilder();
            csv.AppendLine("Id,DeviceId,MetricName,NumericValue,StringValue,Unit,DeviceType,TenantId,Timestamp");

            foreach (var record in result.Records)
            {
                csv.AppendLine(string.Join(",",
                    record.Id,
                    EscapeCsvField(record.DeviceId),
                    EscapeCsvField(record.MetricName),
                    record.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? "",
                    EscapeCsvField(record.StringValue ?? ""),
                    EscapeCsvField(record.Unit ?? ""),
                    EscapeCsvField(record.DeviceType ?? ""),
                    record.TenantId?.ToString() ?? "",
                    record.Timestamp.ToString("O")
                ));
            }

            var fileName = $"telemetry-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
            return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", fileName);
        }

        // Default: return JSON
        return Ok(new
        {
            records = result.Records.Select(r => new
            {
                id = r.Id,
                deviceId = r.DeviceId,
                metricName = r.MetricName,
                numericValue = r.NumericValue,
                stringValue = r.StringValue,
                jsonValue = r.JsonValue,
                unit = r.Unit,
                tenantId = r.TenantId,
                deviceType = r.DeviceType,
                tags = r.Tags,
                timestamp = r.Timestamp
            }),
            totalCount = result.TotalCount
        });
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
}

// Request DTOs

public class IngestTelemetryRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string MetricName { get; set; } = string.Empty;
    public double? NumericValue { get; set; }
    public string? StringValue { get; set; }
    public string? JsonValue { get; set; }
    public string? Unit { get; set; }
    public Guid? TenantId { get; set; }
    public string? DeviceType { get; set; }
    public string? Tags { get; set; }
    public DateTime? Timestamp { get; set; }
}

public class BatchIngestTelemetryRequest
{
    public List<IngestTelemetryRequest> Records { get; set; } = new();
}

public class CleanupRequest
{
    public int RetentionDays { get; set; } = 90;
}
