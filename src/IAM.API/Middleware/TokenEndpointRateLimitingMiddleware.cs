using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace IAM.API.Middleware;

/// <summary>
/// Task 4097: brute-force protection for the OpenIddict token endpoint.
///
/// OpenIddict rejects invalid token requests (wrong client_secret, bad grants) inside
/// UseAuthentication, BEFORE the general <see cref="RateLimitingMiddleware"/> runs, so failed
/// client authentication was never rate limited: with a weak or leaked client secret this allowed
/// unlimited online guessing. This middleware therefore runs ahead of UseAuthentication and
/// throttles /connect/token per source IP.
///
/// Design:
/// - Only FAILED attempts (4xx responses from the token endpoint) count against the limit, so a
///   success-heavy legitimate client is never throttled by its own traffic.
/// - Keyed by source IP only (RemoteIpAddress AFTER UseForwardedHeaders has rewritten it from the
///   trusted proxy's X-Forwarded-For, never a raw header read, so the key is not spoofable).
///   client_id is deliberately NOT part of the key: it is attacker-chosen, so including it would
///   let an attacker escape the limit by rotating client ids. Per-IP keying also guarantees one
///   abusive IP can never lock a legitimate client out globally: other IPs have their own bucket.
/// - Requests blocked with 429 are NOT counted as failures, so a blocked attacker does not extend
///   its own block; the window drains after <see cref="WindowSizeSeconds"/> seconds.
/// </summary>
public class TokenEndpointRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TokenEndpointRateLimitingMiddleware> _logger;

    // 10 failed attempts per minute per IP: far above what a legitimate client produces (an
    // occasional expired refresh token or misconfigured secret), far below anything useful for
    // online secret guessing (~14k attempts/day at full speed).
    private const int DefaultFailureLimit = 10;
    private const int WindowSizeSeconds = 60;

    private readonly int _failureLimit;

    public TokenEndpointRateLimitingMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        ILogger<TokenEndpointRateLimitingMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
        _failureLimit = configuration.GetValue("RateLimiting:TokenEndpointFailureLimitPerMinute", DefaultFailureLimit);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/connect/token"))
        {
            await _next(context);
            return;
        }

        // Must be read AFTER UseForwardedHeaders (this middleware is registered later in the
        // pipeline), so behind the trusted reverse proxy this is the real client IP.
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var cacheKey = $"tokenfail:{ip}";

        var state = GetState(cacheKey);
        var now = DateTime.UtcNow;
        int retryAfter = 0;
        bool blocked = false;

        lock (state.Lock)
        {
            state.FailureTimes.RemoveAll(t => t < now.AddSeconds(-WindowSizeSeconds));

            if (state.FailureTimes.Count >= _failureLimit)
            {
                blocked = true;
                var resetAt = state.FailureTimes[0].AddSeconds(WindowSizeSeconds);
                retryAfter = Math.Max(1, (int)Math.Ceiling((resetAt - now).TotalSeconds));
            }
        }

        if (blocked)
        {
            _logger.LogWarning(
                "Token endpoint rate limit exceeded for IP {ClientIp}: {Limit}+ failed attempts in the last {Window}s",
                ip, _failureLimit, WindowSizeSeconds);

            context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            context.Response.Headers["Retry-After"] = retryAfter.ToString();
            context.Response.ContentType = "application/json";

            // Blocked requests are intentionally not recorded: the window drains while blocked.
            // Body follows the OAuth2 error shape so token clients can parse it.
            await context.Response.WriteAsync(
                """{"error":"temporarily_unavailable","error_description":"Too many failed token requests from this address. Please try again later."}""");
            return;
        }

        await _next(context);

        // Count only failures (4xx: invalid_client / invalid_grant / invalid_request). Successful
        // token issuance (200) never counts, so legitimate clients are unaffected at any rate
        // below the general RateLimitingMiddleware limits.
        if (context.Response.StatusCode >= 400 && context.Response.StatusCode < 500)
        {
            lock (state.Lock)
            {
                state.FailureTimes.RemoveAll(t => t < DateTime.UtcNow.AddSeconds(-WindowSizeSeconds));
                state.FailureTimes.Add(DateTime.UtcNow);

                if (state.FailureTimes.Count == _failureLimit)
                {
                    _logger.LogWarning(
                        "Token endpoint failure threshold reached for IP {ClientIp}: {Limit} failed attempts in {Window}s, subsequent requests will be throttled",
                        ip, _failureLimit, WindowSizeSeconds);
                }
            }
        }
    }

    private FailureWindowState GetState(string cacheKey)
    {
        return _cache.GetOrCreate(cacheKey, entry =>
        {
            // Sliding expiration: the entry disappears once an IP has been quiet for two windows.
            entry.SlidingExpiration = TimeSpan.FromSeconds(WindowSizeSeconds * 2);
            return new FailureWindowState();
        })!;
    }

    private sealed class FailureWindowState
    {
        public readonly object Lock = new();
        public List<DateTime> FailureTimes { get; } = new();
    }
}

/// <summary>
/// Extension method for clean middleware registration.
/// </summary>
public static class TokenEndpointRateLimitingMiddlewareExtensions
{
    public static IApplicationBuilder UseTokenEndpointRateLimiting(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TokenEndpointRateLimitingMiddleware>();
    }
}
