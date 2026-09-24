using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace IAM.API.Middleware;

/// <summary>
/// Sliding window rate limiter using IMemoryCache.
/// Tracks requests by JWT subject claim or IP address. API-key requests are limited per key by
/// Hazina.Security.ApiKeys (Microsoft.AspNetCore.RateLimiting) and skipped here.
/// Returns 429 Too Many Requests with appropriate rate limit headers when exceeded.
/// </summary>
public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RateLimitingMiddleware> _logger;

    // The anonymous limit must accommodate a full interactive OIDC login flow
    // (authorize redirect + login SPA API calls + retries) from a single IP.
    // Brute-force protection lives at the service level (account lockout, OTP
    // max-attempts, per-email magic-link limits), not in this blanket limiter.
    private const int DefaultAuthenticatedLimit = 300; // requests per minute
    private const int DefaultAnonymousLimit = 120;     // requests per minute
    private const int WindowSizeSeconds = 60;

    private readonly int _authenticatedLimit;
    private readonly int _anonymousLimit;

    public RateLimitingMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        ILogger<RateLimitingMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
        _authenticatedLimit = configuration.GetValue("RateLimiting:AuthenticatedLimitPerMinute", DefaultAuthenticatedLimit);
        _anonymousLimit = configuration.GetValue("RateLimiting:AnonymousLimitPerMinute", DefaultAnonymousLimit);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Health checks must never be throttled (monitoring shares an IP with everything else on the host)
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        // Per-key limiting for API-key callers is done by the shared middleware (Hazina.Security.ApiKeys).
        if (context.Items.ContainsKey(Hazina.Security.ApiKeys.ApiKeyDefaults.RecordItemKey))
        {
            await _next(context);
            return;
        }

        var clientKey = GetClientIdentifier(context);
        var limit = GetRateLimit(context);
        var cacheKey = $"rl:{clientKey}";

        var (isAllowed, remaining, resetAt) = CheckRateLimit(cacheKey, limit);

        // Always add rate limit headers
        context.Response.Headers["X-RateLimit-Limit"] = limit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, remaining).ToString();
        context.Response.Headers["X-RateLimit-Reset"] = new DateTimeOffset(resetAt).ToUnixTimeSeconds().ToString();

        if (!isAllowed)
        {
            _logger.LogWarning(
                "Rate limit exceeded for client {ClientKey}. Limit: {Limit}/min",
                clientKey, limit);

            context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            context.Response.Headers["Retry-After"] = ((int)(resetAt - DateTime.UtcNow).TotalSeconds).ToString();
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync(
                """{"error":"Rate limit exceeded","message":"Too many requests. Please try again later."}""");
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Determine the client identifier for rate limiting.
    /// Priority: JWT sub claim > IP address.
    /// </summary>
    private static string GetClientIdentifier(HttpContext context)
    {
        // 1. JWT subject claim (authenticated user)
        var subClaim = context.User?.FindFirst("sub")?.Value
                    ?? context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(subClaim))
            return $"user:{subClaim}";

        // 2. IP address (anonymous)
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }

    /// <summary>
    /// Determine the rate limit for this request.
    /// Authenticated users get the higher configured limit; anonymous callers the lower one.
    /// </summary>
    private int GetRateLimit(HttpContext context)
    {
        // Authenticated users get higher limits
        if (context.User?.Identity?.IsAuthenticated == true)
            return _authenticatedLimit;

        return _anonymousLimit;
    }

    /// <summary>
    /// Check and update the sliding window rate limit counter.
    /// Uses a list of timestamps within the current window.
    /// </summary>
    private (bool IsAllowed, int Remaining, DateTime ResetAt) CheckRateLimit(string cacheKey, int limit)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddSeconds(-WindowSizeSeconds);
        var resetAt = now.AddSeconds(WindowSizeSeconds);

        // Get or create the request timestamps list
        var timestamps = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2); // Cleanup buffer
            return new SlidingWindowState();
        })!;

        lock (timestamps.Lock)
        {
            // Remove entries outside the window
            timestamps.RequestTimes.RemoveAll(t => t < windowStart);

            var currentCount = timestamps.RequestTimes.Count;

            if (currentCount >= limit)
            {
                // Find when the oldest request in the window will expire
                if (timestamps.RequestTimes.Count > 0)
                {
                    resetAt = timestamps.RequestTimes[0].AddSeconds(WindowSizeSeconds);
                }

                return (false, 0, resetAt);
            }

            // Record this request
            timestamps.RequestTimes.Add(now);

            var remaining = limit - timestamps.RequestTimes.Count;
            return (true, remaining, resetAt);
        }
    }

    /// <summary>
    /// Thread-safe sliding window state stored in memory cache.
    /// </summary>
    private class SlidingWindowState
    {
        public readonly object Lock = new();
        public List<DateTime> RequestTimes { get; } = new();
    }
}

/// <summary>
/// Extension method for clean middleware registration.
/// </summary>
public static class RateLimitingMiddlewareExtensions
{
    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RateLimitingMiddleware>();
    }
}
