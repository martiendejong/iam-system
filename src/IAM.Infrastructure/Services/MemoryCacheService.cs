using System.Collections.Concurrent;
using IAM.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<MemoryCacheService> _logger;
    private readonly ConcurrentDictionary<string, byte> _keys = new();
    private long _hits;
    private long _misses;

    public MemoryCacheService(IMemoryCache cache, ILogger<MemoryCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out T? value))
        {
            Interlocked.Increment(ref _hits);
            return Task.FromResult(value);
        }

        Interlocked.Increment(ref _misses);
        return Task.FromResult(default(T?));
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        var options = new MemoryCacheEntryOptions();

        if (expiry.HasValue)
        {
            options.AbsoluteExpirationRelativeToNow = expiry.Value;
        }
        else
        {
            // Default 5-minute sliding expiration when no explicit expiry
            options.SlidingExpiration = TimeSpan.FromMinutes(5);
        }

        // Track key removal for prefix deletion support
        options.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            _keys.TryRemove(evictedKey.ToString()!, out _);
        });

        _cache.Set(key, value, options);
        _keys.TryAdd(key, 0);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _cache.Remove(key);
        _keys.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var keysToRemove = _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
        }

        _logger.LogDebug("Removed {Count} keys with prefix {Prefix} from memory cache", keysToRemove.Count, prefix);

        return Task.CompletedTask;
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _misses);

        var result = await factory();
        await SetAsync(key, result, expiry, ct);

        return result;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(_cache.TryGetValue(key, out _));
    }

    public Task<long> IncrementAsync(string key, long value = 1, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        // Use the key tracking dictionary for atomic increment
        var current = _cache.GetOrCreate(key, entry =>
        {
            if (expiry.HasValue)
            {
                entry.AbsoluteExpirationRelativeToNow = expiry.Value;
            }
            else
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(5);
            }

            entry.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
            {
                _keys.TryRemove(evictedKey.ToString()!, out _);
            });

            _keys.TryAdd(key, 0);
            return 0L;
        });

        var newValue = current + value;
        _cache.Set(key, newValue, expiry.HasValue
            ? new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiry.Value }
            : new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) });
        _keys.TryAdd(key, 0);

        return Task.FromResult(newValue);
    }

    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var stats = new CacheStatistics
        {
            TotalKeys = _keys.Count,
            Hits = Interlocked.Read(ref _hits),
            Misses = Interlocked.Read(ref _misses),
            MemoryUsage = "in-process (memory cache fallback)",
            IsConnected = true
        };

        return Task.FromResult(stats);
    }
}
