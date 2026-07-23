using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAM.API.Tests;

/// <summary>
/// EF InMemory-backed tests for the parts of TelemetryStorageService that are plain LINQ
/// (no raw SQL) - the same pattern used by SessionServiceTests elsewhere in this project.
/// </summary>
public class TelemetryStorageServiceTests
{
    private static TelemetryStorageService CreateService(string dbName)
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var context = new IAMDbContext(options);
        return new TelemetryStorageService(context, NullLogger<TelemetryStorageService>.Instance);
    }

    [Fact]
    public async Task IngestAsync_AssignsIdAndTimestamp_WhenNotProvided()
    {
        var service = CreateService(nameof(IngestAsync_AssignsIdAndTimestamp_WhenNotProvided));
        var record = new TelemetryRecord { DeviceId = "d1", MetricName = "temperature", NumericValue = 21.5 };

        await service.IngestAsync(record);

        Assert.NotEqual(Guid.Empty, record.Id);
        Assert.NotEqual(default, record.Timestamp);
    }

    [Fact]
    public async Task IngestBatchAsync_PersistsAllRecords()
    {
        var service = CreateService(nameof(IngestBatchAsync_PersistsAllRecords));
        var records = Enumerable.Range(0, 50)
            .Select(i => new TelemetryRecord { DeviceId = $"d{i % 5}", MetricName = "temperature", NumericValue = i })
            .ToList();

        await service.IngestBatchAsync(records);
        var stats = await service.GetStatisticsAsync();

        Assert.Equal(50, stats.TotalRecords);
        Assert.Equal(5, stats.UniqueDevices);
    }

    [Fact]
    public async Task GetMetricNamesAsync_ReturnsDistinctSortedNames()
    {
        var service = CreateService(nameof(GetMetricNamesAsync_ReturnsDistinctSortedNames));
        await service.IngestBatchAsync(new List<TelemetryRecord>
        {
            new() { DeviceId = "d1", MetricName = "temperature" },
            new() { DeviceId = "d1", MetricName = "humidity" },
            new() { DeviceId = "d1", MetricName = "temperature" },
        });

        var metrics = await service.GetMetricNamesAsync("d1");

        Assert.Equal(new[] { "humidity", "temperature" }, metrics);
    }

    [Fact]
    public async Task CleanupOldDataAsync_DeletesOnlyRecordsOlderThanRetention()
    {
        var service = CreateService(nameof(CleanupOldDataAsync_DeletesOnlyRecordsOlderThanRetention));
        await service.IngestBatchAsync(new List<TelemetryRecord>
        {
            new() { DeviceId = "d1", MetricName = "m", Timestamp = DateTime.UtcNow.AddDays(-100) },
            new() { DeviceId = "d1", MetricName = "m", Timestamp = DateTime.UtcNow.AddDays(-1) },
        });

        var deleted = await service.CleanupOldDataAsync(retentionDays: 90);
        var stats = await service.GetStatisticsAsync();

        Assert.Equal(1, deleted);
        Assert.Equal(1, stats.TotalRecords);
    }
}

/// <summary>
/// Integration tests against the real local Postgres instance for the two methods that use
/// raw SQL (date_bin bucketing, DISTINCT ON) - EF InMemory cannot execute either. Scoped to a
/// unique per-run device id and cleaned up afterward so this never touches other data in the
/// shared dev database.
/// </summary>
public class TelemetryStorageServicePostgresTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=127.0.0.1;Port=5432;Database=iam_db;Username=iam_user;Password=IAM_Db_P@ss2026!";

    private readonly string _deviceId = $"xunit-test-{Guid.NewGuid():N}";
    private IAMDbContext _context = null!;
    private TelemetryStorageService _service = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>().UseNpgsql(ConnectionString).Options;
        _context = new IAMDbContext(options);
        _service = new TelemetryStorageService(_context, NullLogger<TelemetryStorageService>.Instance);

        await _service.IngestBatchAsync(new List<TelemetryRecord>
        {
            new() { DeviceId = _deviceId, MetricName = "temperature", NumericValue = 20, DeviceType = "sensor", Timestamp = DateTime.UtcNow.AddMinutes(-10) },
            new() { DeviceId = _deviceId, MetricName = "temperature", NumericValue = 24, DeviceType = "sensor", Timestamp = DateTime.UtcNow.AddMinutes(-5) },
            new() { DeviceId = _deviceId, MetricName = "temperature", NumericValue = 22, DeviceType = "sensor", Timestamp = DateTime.UtcNow },
            new() { DeviceId = _deviceId, MetricName = "humidity", NumericValue = 55, DeviceType = "sensor", Timestamp = DateTime.UtcNow },
        });
    }

    public async Task DisposeAsync()
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"TelemetryRecords\" WHERE \"DeviceId\" = {_deviceId}");
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsOneRowPerMetric_WithMostRecentValue()
    {
        var latest = await _service.GetLatestAsync(_deviceId);

        Assert.Equal(2, latest.Count);
        var temperature = latest.Single(r => r.MetricName == "temperature");
        Assert.Equal(22, temperature.NumericValue);
    }

    [Fact]
    public async Task GetLatestAsync_SingleMetric_ReturnsMostRecentValue()
    {
        var latest = await _service.GetLatestAsync(_deviceId, "temperature");

        Assert.NotNull(latest);
        Assert.Equal(22, latest!.NumericValue);
    }

    [Fact]
    public async Task AggregateAsync_ComputesAverageAcrossBuckets()
    {
        var result = await _service.AggregateAsync(new TelemetryAggregationQuery
        {
            DeviceId = _deviceId,
            MetricName = "temperature",
            Aggregation = "avg",
            Interval = "1h",
            StartTime = DateTime.UtcNow.AddHours(-1),
            EndTime = DateTime.UtcNow.AddMinutes(1)
        });

        Assert.Single(result);
        Assert.Equal(3, result[0].Count);
        Assert.Equal(22, result[0].Value, precision: 5); // (20 + 24 + 22) / 3
    }
}
