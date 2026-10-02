using System.Globalization;
using System.Text;
using IAM.API.Authorization;
using IAM.API.Hubs;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TelemetryController : ControllerBase
{
    private readonly ITelemetryStorageService _telemetryService;
    private readonly IHubContext<TelemetryHub> _telemetryHub;
    private readonly ITelemetryAccessAuthorizer _authorizer;

    public TelemetryController(
        ITelemetryStorageService telemetryService,
        IHubContext<TelemetryHub> telemetryHub,
        ITelemetryAccessAuthorizer authorizer)
    {
        _telemetryService = telemetryService;
        _telemetryHub = telemetryHub;
        _authorizer = authorizer;
    }

    private ObjectResult Denied(string? message) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = message ?? "Forbidden" });

    /// <summary>
    /// The tenant a tenant-wide read is pinned to (task 4708): the caller's own tenant, the one they ask for
    /// when they belong to it, or null (all tenants) for a SuperAdmin who asks for none.
    /// </summary>
    private async Task<(Guid? TenantId, IActionResult? Failure)> ResolveReadScopeAsync(
        Guid? requestedTenantId, CancellationToken ct)
    {
        var scope = await _authorizer.ResolveReadScopeAsync(User, requestedTenantId, ct);
        return scope.Decision switch
        {
            TelemetryScopeDecision.Allowed => (scope.TenantId, null),
            TelemetryScopeDecision.TenantRequired => (null, BadRequest(new { error = scope.Message })),
            _ => (null, Denied(scope.Message))
        };
    }

    private Task BroadcastAsync(TelemetryRecord record, CancellationToken ct)
    {
        var message = new
        {
            record.DeviceId,
            record.DeviceType,
            record.TenantId,
            DataType = record.MetricName,
            Payload = (object?)record.NumericValue ?? record.StringValue ?? record.JsonValue,
            record.Timestamp,
            receivedAt = DateTime.UtcNow
        };

        // Records are built from the device registry, so tenant and type are always set here.
        return Task.WhenAll(
            _telemetryHub.Clients.Group(TelemetryGroups.Device(record.DeviceId)).SendAsync("TelemetryReceived", message, ct),
            _telemetryHub.Clients.Group(TelemetryGroups.Tenant(record.TenantId!.Value)).SendAsync("TelemetryReceived", message, ct),
            _telemetryHub.Clients.Group(TelemetryGroups.DeviceType(record.TenantId!.Value, record.DeviceType ?? string.Empty)).SendAsync("TelemetryReceived", message, ct));
    }

    private static TelemetryRecord ToRecord(IngestTelemetryRequest request, TelemetryDevice device) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = device.DeviceId,
        MetricName = request.MetricName,
        NumericValue = request.NumericValue,
        StringValue = request.StringValue,
        JsonValue = request.JsonValue,
        Unit = request.Unit,
        // Tenant and type come from the device registry, never from the request body.
        TenantId = device.TenantId,
        DeviceType = device.DeviceType,
        Tags = request.Tags,
        Timestamp = request.Timestamp ?? DateTime.UtcNow
    };

    /// <summary>
    /// Ingest a single telemetry record from a device. A device token works only for its own device;
    /// a user needs SuperAdmin, BuildingOwner or BuildingManager of the device's tenant.
    /// </summary>
    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromBody] IngestTelemetryRequest request, CancellationToken ct)
    {
        var access = await _authorizer.AuthorizeDeviceAsync(User, request.DeviceId, TelemetryAction.Write, ct);
        if (!access.Allowed)
            return Denied(access.Message);

        var record = ToRecord(request, access.Device!);

        await _telemetryService.IngestAsync(record, ct);
        await BroadcastAsync(record, ct);

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
    /// Ingest a batch of telemetry records (up to 10,000 per request).
    /// </summary>
    [HttpPost("ingest/batch")]
    public async Task<IActionResult> IngestBatch([FromBody] BatchIngestTelemetryRequest request, CancellationToken ct)
    {
        if (request.Records == null || request.Records.Count == 0)
        {
            return BadRequest(new { error = "No records provided" });
        }

        if (request.Records.Count > 10_000)
        {
            return BadRequest(new { error = "Maximum 10,000 records per batch. Received: " + request.Records.Count });
        }

        // Every device in the batch must be authorized before anything is stored: one refusal rejects it all.
        var devices = new Dictionary<string, TelemetryDevice>(StringComparer.Ordinal);
        foreach (var deviceId in request.Records.Select(r => r.DeviceId).Distinct(StringComparer.Ordinal))
        {
            var access = await _authorizer.AuthorizeDeviceAsync(User, deviceId, TelemetryAction.Write, ct);
            if (!access.Allowed)
                return Denied(access.Message);

            devices[deviceId] = access.Device!;
        }

        var records = request.Records.Select(r => ToRecord(r, devices[r.DeviceId])).ToList();

        await _telemetryService.IngestBatchAsync(records, ct);

        // Broadcast one "latest point" message per device rather than one per record -
        // a 10K-record batch would otherwise flood SignalR clients with 10K individual sends.
        await Task.WhenAll(records
            .GroupBy(r => r.DeviceId)
            .Select(g => BroadcastAsync(g.OrderByDescending(r => r.Timestamp).First(), ct)));

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
        var (scopedTenantId, failure) = await ResolveReadScopeAsync(tenantId, ct);
        if (failure != null)
            return failure;

        var query = new TelemetryQuery
        {
            DeviceId = deviceId,
            MetricName = metricName,
            TenantId = scopedTenantId,
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

        var (scopedTenantId, failure) = await ResolveReadScopeAsync(tenantId, ct);
        if (failure != null)
            return failure;

        var query = new TelemetryAggregationQuery
        {
            DeviceId = deviceId,
            MetricName = metricName,
            TenantId = scopedTenantId,
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
    /// Latest value for every metric reported by a device.
    /// </summary>
    [HttpGet("latest/{deviceId}")]
    public async Task<IActionResult> GetLatest(string deviceId, CancellationToken ct)
    {
        var access = await _authorizer.AuthorizeDeviceAsync(User, deviceId, TelemetryAction.Read, ct);
        if (!access.Allowed)
            return Denied(access.Message);

        var records = await _telemetryService.GetLatestAsync(access.Device!.DeviceId, ct);

        return Ok(new
        {
            deviceId,
            metrics = records.Select(r => new
            {
                metricName = r.MetricName,
                numericValue = r.NumericValue,
                stringValue = r.StringValue,
                jsonValue = r.JsonValue,
                unit = r.Unit,
                deviceType = r.DeviceType,
                tenantId = r.TenantId,
                tags = r.Tags,
                timestamp = r.Timestamp
            })
        });
    }

    /// <summary>
    /// List available metric names (optionally filtered by device).
    /// </summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetricNames([FromQuery] string? deviceId, CancellationToken ct)
    {
        Guid? scopedTenantId;
        if (!string.IsNullOrEmpty(deviceId))
        {
            // One device: the caller must be allowed to read it; its tenant pins the lookup.
            var access = await _authorizer.AuthorizeDeviceAsync(User, deviceId, TelemetryAction.Read, ct);
            if (!access.Allowed)
                return Denied(access.Message);

            deviceId = access.Device!.DeviceId;
            scopedTenantId = access.Device.TenantId;
        }
        else
        {
            var (tenantScope, failure) = await ResolveReadScopeAsync(null, ct);
            if (failure != null)
                return failure;

            scopedTenantId = tenantScope;
        }

        var metrics = await _telemetryService.GetMetricNamesAsync(deviceId, scopedTenantId, ct);

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
        var (scopedTenantId, failure) = await ResolveReadScopeAsync(tenantId, ct);
        if (failure != null)
            return failure;

        var stats = await _telemetryService.GetStatisticsAsync(scopedTenantId, ct);

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
    /// Trigger cleanup of old telemetry data (SuperAdmin/SystemAdmin only: it deletes across all tenants).
    /// </summary>
    [HttpPost("cleanup")]
    [Authorize(Roles = "SuperAdmin,SystemAdmin")]
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
        var (scopedTenantId, failure) = await ResolveReadScopeAsync(tenantId, ct);
        if (failure != null)
            return failure;

        var query = new TelemetryQuery
        {
            DeviceId = deviceId,
            MetricName = metricName,
            TenantId = scopedTenantId,
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
