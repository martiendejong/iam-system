using System.Security.Cryptography;
using System.Text;
using IAM.Core.Services;
using Microsoft.Extensions.Caching.Memory;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 5566: opaque random challenge tokens held in memory (hashed key), valid for <see cref="LifetimeMinutes"/>
/// minutes. The IAM API runs as a single instance, so process memory is enough; a restart only means the user
/// signs in again. Register as a singleton.
/// </summary>
public sealed class TwoFactorChallengeService : ITwoFactorChallengeService
{
    public const int LifetimeMinutes = 10;
    private const string Purpose = "login-2fa";

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly object _lock = new();

    private sealed record Challenge(Guid UserId, DateTimeOffset ExpiresAt);

    public TwoFactorChallengeService(IMemoryCache cache, TimeProvider? timeProvider = null)
    {
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Issue(Guid userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = _time.GetUtcNow().AddMinutes(LifetimeMinutes);
        // Relative TTL (the cache's own clock); the exact expiry is still checked against the injected clock.
        _cache.Set(Key(token), new Challenge(userId, expires),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(LifetimeMinutes + 1) });
        return token;
    }

    public bool IsValid(string? token, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        return _cache.TryGetValue(Key(token), out Challenge? c) && c is not null
            && c.UserId == userId && c.ExpiresAt > _time.GetUtcNow();
    }

    public bool TryConsume(string? token, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        lock (_lock)
        {
            if (!IsValid(token, userId)) return false;
            _cache.Remove(Key(token));
            return true;
        }
    }

    private static string Key(string token) =>
        $"{Purpose}:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
