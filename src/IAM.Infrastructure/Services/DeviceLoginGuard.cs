using IAM.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 5159: in-memory failure counter and one-time nonce store for device login (modelled on LoginThrottle, 5166).
///
/// - Failures are counted per device id. <see cref="DefaultMaxFailedAttempts"/> failures within the window lock that
///   device for <see cref="DefaultLockoutMinutes"/> minutes; every other device is unaffected. A lock is not extended
///   by further attempts while it lasts, and a correct login clears the count.
/// - A nonce works once per device. It is remembered for longer than the HMAC timestamp window on both sides of "now",
///   so a request that is still accepted by its timestamp can never be replayed.
///
/// The state lives in IMemoryCache, whose own expiry bounds the memory. It is therefore per process: with several API
/// instances behind one address each instance counts and remembers on its own, so a lock needs its attempts to land on
/// the same instance and a replay can pass once on every instance. A shared store is the follow-up for that.
/// </summary>
public sealed class DeviceLoginGuard : IDeviceLoginGuard
{
    internal const int DefaultMaxFailedAttempts = 5;
    internal const int DefaultFailureWindowMinutes = 15;
    internal const int DefaultLockoutMinutes = 15;

    /// <summary>How far a request timestamp may be from the server clock, in either direction (see DeviceAuthenticationService).</summary>
    internal static readonly TimeSpan TimestampWindow = TimeSpan.FromMinutes(5);

    // Both sides of the window plus a minute of slack: the earliest accepted use of a timestamp and the latest differ by 2 x window.
    internal static readonly TimeSpan NonceLifetime = TimestampWindow + TimestampWindow + TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly int _maxFailedAttempts;
    private readonly TimeSpan _failureWindow;
    private readonly TimeSpan _lockout;

    public DeviceLoginGuard(IMemoryCache cache, IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
        _maxFailedAttempts = Math.Max(1, configuration.GetValue("DeviceAuth:MaxFailedAttempts", DefaultMaxFailedAttempts));
        _failureWindow = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("DeviceAuth:FailureWindowMinutes", DefaultFailureWindowMinutes)));
        _lockout = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("DeviceAuth:LockoutMinutes", DefaultLockoutMinutes)));
    }

    public bool IsLockedOut(string deviceId)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(FailureKey(deviceId), out FailureState? state)
                && state!.LockedUntil > _time.GetUtcNow();
        }
    }

    public void RecordFailure(string deviceId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var key = FailureKey(deviceId);
            _cache.TryGetValue(key, out FailureState? state);

            if (state is not null)
            {
                if (state.LockedUntil > now)
                {
                    return; // already locked: attempts during the lock do not extend it
                }

                // The lock ran out, or the failures are older than the window: start counting again.
                if (state.LockedUntil is not null || state.WindowEnds <= now)
                {
                    state = null;
                }
            }

            state ??= new FailureState();
            state.Count++;

            TimeSpan keepFor;
            if (state.Count >= _maxFailedAttempts)
            {
                state.LockedUntil = now + _lockout;
                keepFor = _lockout;
            }
            else
            {
                state.WindowEnds = now + _failureWindow;
                keepFor = _failureWindow;
            }

            // The entry's own expiry only frees memory; every decision above compares against the injected clock.
            _cache.Set(key, state, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = keepFor });
        }
    }

    public void RecordSuccess(string deviceId)
    {
        lock (_gate)
        {
            _cache.Remove(FailureKey(deviceId));
        }
    }

    public bool TryUseNonce(string deviceId, string nonce)
    {
        // Length-prefixed, so no (device, nonce) pair can collide with another by moving characters across the separator.
        var key = "device-nonce:" + deviceId.Length + ":" + deviceId + nonce;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out DateTimeOffset usedUntil) && usedUntil > now)
            {
                return false;
            }

            _cache.Set(key, now + NonceLifetime, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = NonceLifetime });
            return true;
        }
    }

    private static string FailureKey(string deviceId) => "device-failures:" + deviceId;

    private sealed class FailureState
    {
        public int Count { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
        public DateTimeOffset WindowEnds { get; set; }
    }
}
