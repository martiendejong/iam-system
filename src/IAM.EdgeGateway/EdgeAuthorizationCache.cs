namespace IAM.EdgeGateway;

/// <summary>
/// Local cache for authorization decisions. Enables offline operation
/// by caching device claims and authorization results.
/// </summary>
public class EdgeAuthorizationCache
{
    private readonly Dictionary<string, CachedDeviceClaims> _deviceClaims = new();
    private readonly Dictionary<string, CachedAuthDecision> _authDecisions = new();
    private readonly TimeSpan _claimsTtl;
    private readonly TimeSpan _decisionTtl;
    private readonly object _lock = new();

    private long _hits;
    private long _misses;

    public EdgeAuthorizationCache()
        : this(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5))
    {
    }

    public EdgeAuthorizationCache(TimeSpan claimsTtl, TimeSpan decisionTtl)
    {
        _claimsTtl = claimsTtl;
        _decisionTtl = decisionTtl;
    }

    /// <summary>
    /// Cache device claims data with the configured TTL.
    /// </summary>
    public void CacheDeviceClaims(string deviceId, DeviceClaimsData claims)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(claims);

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _deviceClaims[deviceId] = new CachedDeviceClaims
            {
                Data = claims,
                CachedAt = now,
                ExpiresAt = now + _claimsTtl
            };
        }
    }

    /// <summary>
    /// Retrieve cached device claims. Returns null if not cached or expired.
    /// </summary>
    public DeviceClaimsData? GetDeviceClaims(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        lock (_lock)
        {
            if (_deviceClaims.TryGetValue(deviceId, out var cached))
            {
                if (cached.ExpiresAt > DateTime.UtcNow)
                {
                    Interlocked.Increment(ref _hits);
                    return cached.Data;
                }

                // Expired -- remove entry
                _deviceClaims.Remove(deviceId);
            }

            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    /// <summary>
    /// Cache an authorization decision for a device/resource/action tuple.
    /// </summary>
    public void CacheAuthDecision(string deviceId, string resource, string action, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(action);

        var key = BuildDecisionKey(deviceId, resource, action);

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _authDecisions[key] = new CachedAuthDecision
            {
                Allowed = allowed,
                CachedAt = now,
                ExpiresAt = now + _decisionTtl
            };
        }
    }

    /// <summary>
    /// Retrieve a cached authorization decision. Returns null if not cached or expired.
    /// </summary>
    public bool? GetCachedDecision(string deviceId, string resource, string action)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(action);

        var key = BuildDecisionKey(deviceId, resource, action);

        lock (_lock)
        {
            if (_authDecisions.TryGetValue(key, out var cached))
            {
                if (cached.ExpiresAt > DateTime.UtcNow)
                {
                    Interlocked.Increment(ref _hits);
                    return cached.Allowed;
                }

                // Expired -- remove entry
                _authDecisions.Remove(key);
            }

            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    /// <summary>
    /// Invalidate all cached data for a specific device (claims and decisions).
    /// </summary>
    public void InvalidateDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        lock (_lock)
        {
            _deviceClaims.Remove(deviceId);

            // Remove all decisions for this device
            var keysToRemove = _authDecisions.Keys
                .Where(k => k.StartsWith(deviceId + "::", StringComparison.Ordinal))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _authDecisions.Remove(key);
            }
        }
    }

    /// <summary>
    /// Invalidate all cached data. Used on full re-sync or security events.
    /// </summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _deviceClaims.Clear();
            _authDecisions.Clear();
        }
    }

    /// <summary>
    /// Get current cache statistics.
    /// </summary>
    public CacheStats GetStats()
    {
        lock (_lock)
        {
            var hits = Interlocked.Read(ref _hits);
            var misses = Interlocked.Read(ref _misses);
            var total = hits + misses;

            return new CacheStats
            {
                CachedDevices = _deviceClaims.Count,
                CachedDecisions = _authDecisions.Count,
                Hits = hits,
                Misses = misses,
                HitRate = total > 0 ? (double)hits / total : 0.0
            };
        }
    }

    /// <summary>
    /// Remove all expired entries from the cache.
    /// Called periodically by the sync worker to keep memory usage bounded.
    /// </summary>
    public void CleanExpired()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            var expiredClaims = _deviceClaims
                .Where(kvp => kvp.Value.ExpiresAt <= now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredClaims)
            {
                _deviceClaims.Remove(key);
            }

            var expiredDecisions = _authDecisions
                .Where(kvp => kvp.Value.ExpiresAt <= now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredDecisions)
            {
                _authDecisions.Remove(key);
            }
        }
    }

    /// <summary>
    /// Get all currently tracked device IDs (including expired entries).
    /// Used by the sync worker to know which devices to refresh.
    /// </summary>
    public IReadOnlyList<string> GetTrackedDeviceIds()
    {
        lock (_lock)
        {
            return _deviceClaims.Keys.ToList().AsReadOnly();
        }
    }

    private static string BuildDecisionKey(string deviceId, string resource, string action)
    {
        return $"{deviceId}::{resource}::{action}";
    }
}

public class CachedDeviceClaims
{
    public required DeviceClaimsData Data { get; set; }
    public DateTime CachedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class CachedAuthDecision
{
    public bool Allowed { get; set; }
    public DateTime CachedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class DeviceClaimsData
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string ResourcePath { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class CacheStats
{
    public int CachedDevices { get; set; }
    public int CachedDecisions { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public double HitRate { get; set; }
}
