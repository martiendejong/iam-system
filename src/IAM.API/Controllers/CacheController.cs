using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "SuperAdmin")]
public class CacheController : ControllerBase
{
    private readonly ICacheService _cacheService;
    private readonly ILogger<CacheController> _logger;

    public CacheController(ICacheService cacheService, ILogger<CacheController> logger)
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <summary>
    /// Get cache statistics including hit rate, memory usage, and connection status
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<CacheStatistics>> GetStatistics(CancellationToken cancellationToken = default)
    {
        var stats = await _cacheService.GetStatisticsAsync(cancellationToken);
        return Ok(stats);
    }

    /// <summary>
    /// Flush all cache entries. Use with caution in production
    /// </summary>
    [HttpDelete("flush")]
    public async Task<ActionResult> FlushAll(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Cache flush requested by {User}", User.Identity?.Name ?? "unknown");

        // Remove all known cache key prefixes
        var prefixes = new[]
        {
            CacheKeys.PolicyEvaluation,
            CacheKeys.DeviceClaims,
            CacheKeys.DevicePermissions,
            CacheKeys.UserPermissions,
            CacheKeys.TenantHierarchy,
            CacheKeys.GroupMembers,
            CacheKeys.ApiKeyValidation,
            CacheKeys.RateLimit,
            CacheKeys.SessionValidation
        };

        foreach (var prefix in prefixes)
        {
            await _cacheService.RemoveByPrefixAsync(prefix, cancellationToken);
        }

        _logger.LogInformation("Cache flushed successfully by {User}", User.Identity?.Name ?? "unknown");

        return Ok(new { message = "Cache flushed successfully", prefixesCleared = prefixes.Length });
    }

    /// <summary>
    /// Clear cache entries matching a specific prefix
    /// </summary>
    [HttpDelete("prefix/{prefix}")]
    public async Task<ActionResult> ClearByPrefix(string prefix, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return BadRequest("Prefix is required");

        // Validate prefix is one of the known cache key prefixes (security: prevent arbitrary key scanning)
        var knownPrefixes = new[]
        {
            CacheKeys.PolicyEvaluation,
            CacheKeys.DeviceClaims,
            CacheKeys.DevicePermissions,
            CacheKeys.UserPermissions,
            CacheKeys.TenantHierarchy,
            CacheKeys.GroupMembers,
            CacheKeys.ApiKeyValidation,
            CacheKeys.RateLimit,
            CacheKeys.SessionValidation
        };

        if (!knownPrefixes.Any(kp => prefix.StartsWith(kp, StringComparison.Ordinal)))
        {
            return BadRequest(new
            {
                error = "Unknown cache prefix",
                knownPrefixes
            });
        }

        _logger.LogWarning("Cache clear by prefix '{Prefix}' requested by {User}", prefix, User.Identity?.Name ?? "unknown");

        await _cacheService.RemoveByPrefixAsync(prefix, cancellationToken);

        return Ok(new { message = $"Cache entries with prefix '{prefix}' cleared successfully" });
    }

    /// <summary>
    /// Check Redis connection health and basic cache operations
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous] // Health checks should be accessible for monitoring tools
    public async Task<ActionResult> HealthCheck(CancellationToken cancellationToken = default)
    {
        var healthResult = new CacheHealthResult();

        try
        {
            // Test write
            var testKey = "cache:health:ping";
            var testValue = DateTime.UtcNow.Ticks;
            await _cacheService.SetAsync(testKey, testValue, TimeSpan.FromSeconds(30), cancellationToken);
            healthResult.WriteOk = true;

            // Test read
            var readValue = await _cacheService.GetAsync<long>(testKey, cancellationToken);
            healthResult.ReadOk = readValue == testValue;

            // Test delete
            await _cacheService.RemoveAsync(testKey, cancellationToken);
            var afterDelete = await _cacheService.ExistsAsync(testKey, cancellationToken);
            healthResult.DeleteOk = !afterDelete;

            // Get statistics for connection status
            var stats = await _cacheService.GetStatisticsAsync(cancellationToken);
            healthResult.IsConnected = stats.IsConnected;
            healthResult.MemoryUsage = stats.MemoryUsage;
            healthResult.TotalKeys = stats.TotalKeys;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache health check failed");
            healthResult.Error = ex.Message;
        }

        healthResult.Status = healthResult.WriteOk && healthResult.ReadOk && healthResult.DeleteOk
            ? "healthy"
            : "degraded";

        healthResult.Timestamp = DateTime.UtcNow;

        var statusCode = healthResult.Status == "healthy" ? 200 : 503;
        return StatusCode(statusCode, healthResult);
    }
}

public class CacheHealthResult
{
    public string Status { get; set; } = "unknown";
    public bool IsConnected { get; set; }
    public bool WriteOk { get; set; }
    public bool ReadOk { get; set; }
    public bool DeleteOk { get; set; }
    public string MemoryUsage { get; set; } = string.Empty;
    public long TotalKeys { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; }
}
