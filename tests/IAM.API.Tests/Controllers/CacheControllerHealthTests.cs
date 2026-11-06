using System.Reflection;
using System.Text.Json;
using IAM.API.Controllers;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAM.API.Tests.Controllers;

public class CacheControllerHealthTests
{
    private sealed class FakeCache : ICacheService
    {
        public Exception? Throw { get; set; }
        public int SetCalls { get; private set; }
        private long _value;

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
            => Task.FromResult((T?)(object)_value);
        public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
        {
            SetCalls++;
            if (Throw != null) throw Throw;
            _value = (long)(object)value!;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default) => Task.CompletedTask;
        public Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null, CancellationToken ct = default) => factory();
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> IncrementAsync(string key, long value = 1, TimeSpan? expiry = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<CacheStatistics> GetStatisticsAsync(CancellationToken ct = default)
            => Task.FromResult(new CacheStatistics { IsConnected = true, MemoryUsage = "12.5M", TotalKeys = 4242 });
    }

    private static CacheController Create(FakeCache cache)
        => new(cache, NullLogger<CacheController>.Instance, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Anonymous_healthy_returns_200_with_only_status_and_timestamp()
    {
        var result = (ObjectResult)await Create(new FakeCache()).HealthCheck();
        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("healthy", json);
        Assert.DoesNotContain("12.5M", json);
        Assert.DoesNotContain("4242", json);
        var props = result.Value!.GetType().GetProperties().Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(new[] { "Status", "Timestamp" }, props);
    }

    [Fact]
    public async Task Anonymous_failure_returns_503_without_error_text()
    {
        var cache = new FakeCache { Throw = new InvalidOperationException("connect to redis-internal:6379 failed") };
        var result = (ObjectResult)await Create(cache).HealthCheck();
        Assert.Equal(503, result.StatusCode);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("redis-internal", json);
        Assert.DoesNotContain("6379", json);
        Assert.Contains("unhealthy", json);
    }

    [Fact]
    public async Task Anonymous_calls_are_cached_and_do_not_hit_redis_each_time()
    {
        var cache = new FakeCache();
        var controller = Create(cache);
        await controller.HealthCheck();
        await controller.HealthCheck();
        await controller.HealthCheck();
        Assert.Equal(1, cache.SetCalls);
    }

    [Fact]
    public async Task SuperAdmin_detail_returns_stats_and_error_text()
    {
        var ok = (ObjectResult)await Create(new FakeCache()).HealthCheckDetail();
        var okDetail = Assert.IsType<CacheHealthResult>(ok.Value);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal("12.5M", okDetail.MemoryUsage);
        Assert.Equal(4242, okDetail.TotalKeys);

        var bad = (ObjectResult)await Create(new FakeCache { Throw = new Exception("redis-internal:6379") }).HealthCheckDetail();
        Assert.Equal(503, bad.StatusCode);
        Assert.Contains("redis-internal:6379", ((CacheHealthResult)bad.Value!).Error);
    }

    [Fact]
    public void Only_the_public_health_action_is_anonymous_and_class_stays_SuperAdmin()
    {
        var t = typeof(CacheController);
        Assert.Equal("SuperAdmin", t.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.NotNull(t.GetMethod(nameof(CacheController.HealthCheck))!.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(t.GetMethod(nameof(CacheController.HealthCheckDetail))!.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
