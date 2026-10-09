using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using IAM.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 5166: in-memory throttle for password sign-ins (modelled on
/// TokenEndpointRateLimitingMiddleware, task 4097).
///
/// - Per (typed email, IP): the first <see cref="DefaultFreeFailures"/> failures are free, every
///   further failure makes the pair wait 2, 4, 8 ... up to <see cref="DefaultMaxDelaySeconds"/>
///   seconds before the next attempt is looked at. The pair forgets its failures after
///   <see cref="PairIdleResetSeconds"/> seconds without activity or after a correct password.
/// - Per IP: at most <see cref="DefaultIpFailureLimit"/> failed sign-ins in a sliding
///   <see cref="DefaultIpWindowSeconds"/>-second window, whatever emails were typed. This is what
///   stops one address from guessing across many (or invented) accounts, each of which would
///   otherwise get its own free failures.
///
/// The IP must be RemoteIpAddress AFTER UseForwardedHeaders has rewritten it from the trusted
/// proxy's X-Forwarded-For (never a raw header, so it cannot be spoofed). IPv6 callers are keyed on
/// their /64 and IPv4-mapped addresses on the IPv4 address, so rotating through a block of
/// addresses does not give a fresh allowance.
/// </summary>
public sealed class LoginThrottle : ILoginThrottle
{
    internal const int DefaultFreeFailures = 3;
    internal const int DefaultMaxDelaySeconds = 60;
    internal const int DefaultIpFailureLimit = 30;
    internal const int DefaultIpWindowSeconds = 900;
    internal const int PairIdleResetSeconds = 900;

    private readonly object _createLock = new();
    private readonly IMemoryCache _cache;
    private readonly ILogger<LoginThrottle> _logger;
    private readonly TimeProvider _time;
    private readonly int _freeFailures;
    private readonly int _maxDelaySeconds;
    private readonly int _ipFailureLimit;
    private readonly int _ipWindowSeconds;

    public LoginThrottle(
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<LoginThrottle> logger,
        TimeProvider? timeProvider = null)
    {
        _cache = cache;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _freeFailures = Math.Max(0, configuration.GetValue("RateLimiting:LoginFreeFailures", DefaultFreeFailures));
        _maxDelaySeconds = Math.Max(1, configuration.GetValue("RateLimiting:LoginMaxDelaySeconds", DefaultMaxDelaySeconds));
        // 0 or below disables the per-IP cap (the per-pair delay always stays on).
        _ipFailureLimit = configuration.GetValue("RateLimiting:LoginIpFailureLimit", DefaultIpFailureLimit);
        _ipWindowSeconds = Math.Max(1, configuration.GetValue("RateLimiting:LoginIpWindowSeconds", DefaultIpWindowSeconds));
    }

    public LoginThrottleDecision BeginAttempt(string? email, string? ipAddress)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var ip = NormalizeIp(ipAddress);
        var ipState = GetState<IpState>(IpKey(ip), _ipWindowSeconds * 2);

        lock (ipState.Lock)
        {
            ipState.Attempts.RemoveAll(t => t <= now.AddSeconds(-_ipWindowSeconds));

            // Checked before the pair state is touched, so an IP that is already over its cap
            // cannot grow the cache by cycling through invented emails.
            if (_ipFailureLimit > 0 && ipState.Attempts.Count >= _ipFailureLimit)
            {
                var wait = CeilSeconds(ipState.Attempts[0].AddSeconds(_ipWindowSeconds) - now);
                if (!ipState.CapLogged)
                {
                    ipState.CapLogged = true;
                    _logger.LogWarning(
                        "Login failure cap reached for IP {ClientIp}: {Limit} failed sign-ins in {Window}s, further attempts are refused",
                        ip, _ipFailureLimit, _ipWindowSeconds);
                }
                return LoginThrottleDecision.Wait(wait);
            }

            ipState.CapLogged = false;

            var pair = GetState<PairState>(PairKey(ip, email), PairIdleResetSeconds);
            lock (pair.Lock)
            {
                if (pair.LastAttemptAt.HasValue && now - pair.LastAttemptAt.Value > TimeSpan.FromSeconds(PairIdleResetSeconds))
                {
                    pair.Failures = 0;
                    pair.BlockedUntil = DateTime.MinValue;
                }

                if (pair.BlockedUntil > now)
                {
                    return LoginThrottleDecision.Wait(CeilSeconds(pair.BlockedUntil - now));
                }

                // Count the attempt as a failure up front; RecordSuccess undoes it.
                pair.Failures++;
                pair.LastAttemptAt = now;
                if (pair.Failures > _freeFailures)
                {
                    pair.BlockedUntil = now.AddSeconds(DelaySeconds(pair.Failures - _freeFailures));
                }
            }

            ipState.Attempts.Add(now);
            return LoginThrottleDecision.Allow;
        }
    }

    public void RecordSuccess(string? email, string? ipAddress)
    {
        var ip = NormalizeIp(ipAddress);

        if (_cache.TryGetValue(PairKey(ip, email), out PairState? pair) && pair != null)
        {
            lock (pair.Lock)
            {
                pair.Failures = 0;
                pair.BlockedUntil = DateTime.MinValue;
                pair.LastAttemptAt = null;
            }
        }

        if (_cache.TryGetValue(IpKey(ip), out IpState? ipState) && ipState != null)
        {
            lock (ipState.Lock)
            {
                if (ipState.Attempts.Count > 0)
                {
                    ipState.Attempts.RemoveAt(ipState.Attempts.Count - 1);
                }
            }
        }
    }

    // 2, 4, 8 ... capped; the exponent is clamped so a long failure streak cannot overflow the shift.
    private int DelaySeconds(int failuresPastFree)
    {
        var exponent = Math.Min(failuresPastFree, 20);
        return (int)Math.Min(_maxDelaySeconds, 1L << exponent);
    }

    private static int CeilSeconds(TimeSpan span) => (int)Math.Ceiling(span.TotalSeconds);

    private T GetState<T>(string key, int slidingSeconds) where T : new()
    {
        // IMemoryCache.GetOrCreate is not atomic: two first requests for the same key could each get
        // their own state object and both see an empty counter. Creation is cheap, so serialise it.
        lock (_createLock)
        {
            return _cache.GetOrCreate(key, entry =>
            {
                // Sliding expiration only reclaims memory; the pair also resets itself explicitly.
                entry.SlidingExpiration = TimeSpan.FromSeconds(slidingSeconds);
                return new T();
            })!;
        }
    }

    private static string IpKey(string ip) => $"loginthrottle:ip:{ip}";

    // Email is hashed so a huge attacker-chosen string cannot bloat the cache keys.
    private static string PairKey(string ip, string? email) => $"loginthrottle:pair:{ip}|{HashEmail(email)}";

    private static string HashEmail(string? email)
    {
        var normalized = (email ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    internal static string NormalizeIp(string? ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Key on the /64: that is what a single subscriber is normally handed.
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return $"{new IPAddress(bytes)}/64";
        }

        return address.ToString();
    }

    private sealed class PairState
    {
        public readonly object Lock = new();
        public int Failures;
        public DateTime BlockedUntil = DateTime.MinValue;
        public DateTime? LastAttemptAt;
    }

    private sealed class IpState
    {
        public readonly object Lock = new();
        public List<DateTime> Attempts { get; } = new();
        public bool CapLogged;
    }
}
