using System.Text.Json;
using IAM.Core.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IAM.Infrastructure.Services;

public class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    private const string StatsHitsKey = "cache:stats:hits";
    private const string StatsMissesKey = "cache:stats:misses";

    public RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger)
    {
        _redis = redis;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync(key);

            if (value.IsNullOrEmpty)
            {
                await db.StringIncrementAsync(StatsMissesKey);
                return default;
            }

            await db.StringIncrementAsync(StatsHitsKey);
            return JsonSerializer.Deserialize<T>((string)value!, _jsonOptions);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed for GET {Key}. Falling through", key);
            return default;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout for GET {Key}. Falling through", key);
            return default;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Redis GET for key {Key}", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var serialized = JsonSerializer.Serialize(value, _jsonOptions);
            await db.StringSetAsync(key, serialized, expiry);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed for SET {Key}. Falling through", key);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout for SET {Key}. Falling through", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Redis SET for key {Key}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed for DELETE {Key}. Falling through", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Redis DELETE for key {Key}", key);
        }
    }

    public async Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        try
        {
            var endpoints = _redis.GetEndPoints();
            foreach (var endpoint in endpoints)
            {
                var server = _redis.GetServer(endpoint);
                var db = _redis.GetDatabase();

                var keysToDelete = new List<RedisKey>();

                // Use SCAN (async enumerable) instead of KEYS to avoid blocking Redis
                await foreach (var key in server.KeysAsync(pattern: $"{prefix}*", pageSize: 250))
                {
                    keysToDelete.Add(key);

                    // Delete in batches of 1000 to avoid memory buildup
                    if (keysToDelete.Count >= 1000)
                    {
                        await db.KeyDeleteAsync(keysToDelete.ToArray());
                        keysToDelete.Clear();
                    }
                }

                // Delete remaining keys
                if (keysToDelete.Count > 0)
                {
                    await db.KeyDeleteAsync(keysToDelete.ToArray());
                }
            }
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed for DELETE by prefix {Prefix}. Falling through", prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Redis DELETE by prefix {Prefix}", prefix);
        }
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync(key);

            if (!value.IsNullOrEmpty)
            {
                await db.StringIncrementAsync(StatsHitsKey);
                return JsonSerializer.Deserialize<T>((string)value!, _jsonOptions)!;
            }

            await db.StringIncrementAsync(StatsMissesKey);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed for GetOrSet {Key}. Executing factory directly", key);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout for GetOrSet {Key}. Executing factory directly", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Redis GetOrSet GET for key {Key}. Executing factory", key);
        }

        // Cache miss or Redis failure: call the factory
        var result = await factory();

        // Attempt to cache the result (best effort)
        try
        {
            var db = _redis.GetDatabase();
            var serialized = JsonSerializer.Serialize(result, _jsonOptions);
            await db.StringSetAsync(key, serialized, expiry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache result for key {Key}. Returning uncached result", key);
        }

        return result;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            return await db.KeyExistsAsync(key);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed for EXISTS {Key}. Returning false", key);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Redis EXISTS for key {Key}", key);
            return false;
        }
    }

    public async Task<long> IncrementAsync(string key, long value = 1, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var result = await db.StringIncrementAsync(key, value);

            // Set expiry on first increment (when result equals the increment value)
            if (expiry.HasValue && result == value)
            {
                await db.KeyExpireAsync(key, expiry.Value);
            }

            return result;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed for INCREMENT {Key}. Returning 0", key);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Redis INCREMENT for key {Key}", key);
            return 0;
        }
    }

    public async Task<CacheStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var stats = new CacheStatistics();

        try
        {
            var db = _redis.GetDatabase();
            stats.IsConnected = _redis.IsConnected;

            var hits = await db.StringGetAsync(StatsHitsKey);
            var misses = await db.StringGetAsync(StatsMissesKey);

            stats.Hits = hits.IsNullOrEmpty ? 0 : (long)hits;
            stats.Misses = misses.IsNullOrEmpty ? 0 : (long)misses;

            // Get server info for memory usage and key count
            var endpoints = _redis.GetEndPoints();
            if (endpoints.Length > 0)
            {
                var server = _redis.GetServer(endpoints[0]);
                var info = await server.InfoAsync("memory");
                var keyspace = await server.InfoAsync("keyspace");

                var memorySection = info.FirstOrDefault(s => s.Key == "memory");
                if (memorySection is { Key: not null })
                {
                    var usedMemory = memorySection.FirstOrDefault(p => p.Key == "used_memory_human");
                    stats.MemoryUsage = usedMemory.Value ?? "unknown";
                }

                var keyspaceSection = keyspace.FirstOrDefault(s => s.Key == "keyspace");
                if (keyspaceSection is { Key: not null })
                {
                    // Parse "db0:keys=123,expires=45,avg_ttl=67890"
                    var db0 = keyspaceSection.FirstOrDefault(p => p.Key == "db0");
                    if (!string.IsNullOrEmpty(db0.Value))
                    {
                        var parts = db0.Value.Split(',');
                        var keysPart = parts.FirstOrDefault(p => p.StartsWith("keys="));
                        if (keysPart != null && long.TryParse(keysPart.Replace("keys=", ""), out var totalKeys))
                        {
                            stats.TotalKeys = totalKeys;
                        }
                    }
                }
            }
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed while getting statistics");
            stats.IsConnected = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while getting cache statistics");
            stats.IsConnected = _redis.IsConnected;
        }

        return stats;
    }
}
