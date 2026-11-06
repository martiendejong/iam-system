using IAM.Core.Services;
using Microsoft.Extensions.Caching.Memory;

namespace IAM.Infrastructure.Services;

public sealed class PasskeyChallengeStore : IPasskeyChallengeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private sealed record Entry(PasskeyChallenge Challenge, DateTimeOffset ExpiresAt);

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public PasskeyChallengeStore(IMemoryCache cache) : this(cache, TimeProvider.System) { }

    public PasskeyChallengeStore(IMemoryCache cache, TimeProvider time)
    {
        _cache = cache;
        _time = time;
    }

    public void Put(PasskeyChallengeKind kind, byte[] challenge, Guid? userId)
    {
        var entry = new Entry(new PasskeyChallenge(challenge, userId), _time.GetUtcNow().Add(Lifetime));
        lock (_gate)
        {
            _cache.Set(Key(kind, ToBase64Url(challenge)), entry, Lifetime + TimeSpan.FromSeconds(5));
        }
    }

    public PasskeyChallenge? Take(PasskeyChallengeKind kind, string challengeBase64Url)
    {
        if (string.IsNullOrEmpty(challengeBase64Url))
            return null;

        var key = Key(kind, challengeBase64Url.TrimEnd('='));
        Entry? entry;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out entry) || entry is null)
                return null;
            _cache.Remove(key);
        }

        return entry.ExpiresAt > _time.GetUtcNow() ? entry.Challenge : null;
    }

    public static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Key(PasskeyChallengeKind kind, string challengeB64Url) =>
        $"passkey-challenge:{kind}:{challengeB64Url}";
}
