namespace IAM.Core.Services;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default);
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<long> IncrementAsync(string key, long value = 1, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken ct = default);
}

public class CacheStatistics
{
    public long TotalKeys { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public double HitRate => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) * 100 : 0;
    public string MemoryUsage { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
}
