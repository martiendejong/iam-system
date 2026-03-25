using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace IAM.API.Middleware;

/// <summary>
/// Sliding window rate limiter using IMemoryCache.
/// Tracks requests by API key prefix, JWT subject claim, or IP address.
/// Returns 429 Too Many Requests with appropriate rate limit headers when exceeded.
/// </summary>
public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RateLimitingMiddleware> _logger;

    private const int DefaultAuthenticatedLimit = 100; // requests per minute
    private const int DefaultAnonymousLimit = 20;      // requests per minute
    private const int WindowSizeSeconds = 60;

    public RateLimitingMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
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
    /// Priority: API key prefix > JWT sub claim > IP address.
    /// </summary>
    private static string GetClientIdentifier(HttpContext context)
    {
        // 1. API key prefix (set by ApiKeyAuthenticationMiddleware)
        var apiKeyPrefix = context.Items["ApiKeyPrefix"] as string;
        if (!string.IsNullOrEmpty(apiKeyPrefix))
            return $"apikey:{apiKeyPrefix}";

        // 2. JWT subject claim (authenticated user)
        var subClaim = context.User?.FindFirst("sub")?.Value
                    ?? context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(subClaim))
            return $"user:{subClaim}";

        // 3. IP address (anonymous)
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }

    /// <summary>
    /// Determine the rate limit for this request.
    /// API keys can have custom limits; otherwise use defaults.
    /// </summary>
    private static int GetRateLimit(HttpContext context)
    {
        // Check for custom API key rate limit
        if (context.Items["ApiKeyRateLimit"] is int customLimit)
            return customLimit;

        // Authenticated users get higher limits
        if (context.User?.Identity?.IsAuthenticated == true)
            return DefaultAuthenticatedLimit;

        return DefaultAnonymousLimit;
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
