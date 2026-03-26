using System.Net.Http.Json;
using IAM.SDK.DotNet;

namespace IAM.EdgeGateway;

/// <summary>
/// Edge gateway that proxies authorization requests, caching results locally.
/// Falls back to cached decisions when cloud IAM is unreachable.
/// </summary>
public class EdgeGatewayService
{
    private readonly IamDeviceClient _iamClient;
    private readonly HttpClient _httpClient;
    private readonly EdgeAuthorizationCache _cache;
    private readonly ILogger<EdgeGatewayService> _logger;
    private readonly EdgeGatewayOptions _options;
    private bool _isOnline = true;

    /// <summary>
    /// Whether the cloud IAM is currently reachable.
    /// </summary>
    public bool IsOnline => _isOnline;

    public EdgeGatewayService(
        IamDeviceClient iamClient,
        HttpClient httpClient,
        EdgeAuthorizationCache cache,
        ILogger<EdgeGatewayService> logger,
        EdgeGatewayOptions options)
    {
        _iamClient = iamClient;
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Authorize a device to perform an action on a resource.
    /// Checks the local cache first. On a cache miss, calls the cloud IAM.
    /// If the cloud is unreachable, falls back to cached decisions (offline mode).
    /// </summary>
    public async Task<EdgeAuthResult> AuthorizeDeviceAsync(
        string deviceId, string resource, string action,
        CancellationToken cancellationToken = default)
    {
        // 1. Check the local decision cache
        var cachedDecision = _cache.GetCachedDecision(deviceId, resource, action);
        if (cachedDecision.HasValue)
        {
            _logger.LogDebug(
                "Cache hit for device {DeviceId}, resource {Resource}, action {Action}: {Allowed}",
                deviceId, resource, action, cachedDecision.Value);

            return new EdgeAuthResult
            {
                Allowed = cachedDecision.Value,
                FromCache = true,
                OfflineMode = !_isOnline,
                Reason = "Cached decision"
            };
        }

        // 2. Cache miss -- try calling cloud IAM
        if (_isOnline)
        {
            try
            {
                var authResponse = await _iamClient.AuthorizeAsync(resource, action, cancellationToken);

                // Cache the result
                _cache.CacheAuthDecision(deviceId, resource, action, authResponse.Allowed);

                _logger.LogDebug(
                    "Cloud IAM response for device {DeviceId}, resource {Resource}, action {Action}: {Allowed} ({Reason})",
                    deviceId, resource, action, authResponse.Allowed, authResponse.Reason);

                return new EdgeAuthResult
                {
                    Allowed = authResponse.Allowed,
                    FromCache = false,
                    OfflineMode = false,
                    Reason = authResponse.Reason ?? "Cloud IAM decision"
                };
            }
            catch (Exception ex) when (IsConnectivityException(ex))
            {
                _logger.LogWarning(ex,
                    "Cloud IAM unreachable during authorization for device {DeviceId}. Switching to offline mode",
                    deviceId);
                _isOnline = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error calling cloud IAM for device {DeviceId}, resource {Resource}, action {Action}",
                    deviceId, resource, action);
            }
        }

        // 3. Offline fallback -- try local claims-based authorization
        var claims = _cache.GetDeviceClaims(deviceId);
        if (claims != null)
        {
            var allowed = EvaluateLocalAuthorization(claims, resource, action);

            _logger.LogInformation(
                "Offline authorization for device {DeviceId}, resource {Resource}, action {Action}: {Allowed} (from cached claims)",
                deviceId, resource, action, allowed);

            return new EdgeAuthResult
            {
                Allowed = allowed,
                FromCache = true,
                OfflineMode = true,
                Reason = "Offline decision from cached claims"
            };
        }

        // 4. No cached data at all -- deny by default
        _logger.LogWarning(
            "No cached data for device {DeviceId}. Denying access to {Resource}:{Action} (offline, no cached claims)",
            deviceId, resource, action);

        return new EdgeAuthResult
        {
            Allowed = false,
            FromCache = false,
            OfflineMode = true,
            Reason = "Denied: no cached claims available and cloud IAM is unreachable"
        };
    }

    /// <summary>
    /// Fetch fresh claims from the cloud IAM for a specific device and update the local cache.
    /// </summary>
    public async Task SyncDeviceClaimsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        try
        {
            // The IamDeviceClient doesn't expose GetDeviceClaims, so we call the API directly
            var response = await _httpClient.GetAsync(
                $"/api/device-auth/claims/{Uri.EscapeDataString(deviceId)}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to sync claims for device {DeviceId}: HTTP {StatusCode}",
                    deviceId, (int)response.StatusCode);
                return;
            }

            var claimsResponse = await response.Content.ReadFromJsonAsync<DeviceClaimsResponse>(
                cancellationToken: cancellationToken);

            if (claimsResponse == null)
            {
                _logger.LogWarning("Received null claims response for device {DeviceId}", deviceId);
                return;
            }

            var claimsData = new DeviceClaimsData
            {
                DeviceId = claimsResponse.DeviceId ?? deviceId,
                DeviceType = claimsResponse.DeviceType ?? string.Empty,
                ResourcePath = claimsResponse.ResourcePath ?? string.Empty,
                Permissions = claimsResponse.Permissions ?? new List<string>(),
                Metadata = claimsResponse.Metadata ?? new Dictionary<string, string>()
            };

            _cache.CacheDeviceClaims(deviceId, claimsData);

            _logger.LogDebug(
                "Synced claims for device {DeviceId}: {PermissionCount} permissions",
                deviceId, claimsData.Permissions.Count);
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            _logger.LogWarning(ex, "Cloud IAM unreachable during claims sync for device {DeviceId}", deviceId);
            _isOnline = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync claims for device {DeviceId}", deviceId);
        }
    }

    /// <summary>
    /// Ping the cloud IAM to check connectivity and update online status.
    /// On connectivity restore after an outage, returns true to signal a full re-sync is needed.
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var wasOnline = _isOnline;

        try
        {
            await _iamClient.HeartbeatAsync(cancellationToken);
            _isOnline = true;

            if (!wasOnline)
            {
                _logger.LogInformation("Cloud IAM connectivity restored. Full re-sync recommended");
                return true; // Signal that connectivity was just restored
            }

            _logger.LogDebug("Cloud IAM health check passed");
            return false;
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            if (wasOnline)
            {
                _logger.LogWarning(ex, "Cloud IAM is now unreachable. Switching to offline mode");
            }

            _isOnline = false;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during health check");
            _isOnline = false;
            return false;
        }
    }

    /// <summary>
    /// Authenticate the gateway device itself with the cloud IAM using HMAC.
    /// Must be called on startup and when the token expires.
    /// </summary>
    public async Task<bool> AuthenticateGatewayAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _iamClient.AuthenticateWithHmacAsync(
                _options.SharedSecret, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation("Edge gateway authenticated with cloud IAM as device {DeviceId}",
                    _options.GatewayDeviceId);
                _isOnline = true;
                return true;
            }

            _logger.LogError("Gateway authentication failed: {Error}", result.Error);
            return false;
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            _logger.LogWarning(ex, "Cloud IAM unreachable during gateway authentication. Starting in offline mode");
            _isOnline = false;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during gateway authentication");
            return false;
        }
    }

    /// <summary>
    /// Evaluate authorization locally using cached device claims.
    /// Checks if any of the device's permissions match the requested resource and action.
    /// Uses a simple prefix-based matching strategy consistent with the IAM's hierarchical resource model.
    /// </summary>
    private static bool EvaluateLocalAuthorization(DeviceClaimsData claims, string resource, string action)
    {
        // Build the expected permission pattern: "resource:action"
        var requiredPermission = $"{resource}:{action}";

        foreach (var permission in claims.Permissions)
        {
            // Exact match
            if (string.Equals(permission, requiredPermission, StringComparison.OrdinalIgnoreCase))
                return true;

            // Wildcard match: "resource:*" allows any action on the resource
            var wildcardPermission = $"{resource}:*";
            if (string.Equals(permission, wildcardPermission, StringComparison.OrdinalIgnoreCase))
                return true;

            // Hierarchical prefix match: a permission on a parent resource grants access to child resources
            // e.g., "acme:headquarters:floor-3:*" grants access to "acme:headquarters:floor-3:hvac:unit-247:telemetry:write"
            if (permission.EndsWith(":*", StringComparison.Ordinal))
            {
                var prefix = permission[..^2]; // Remove ":*"
                if (requiredPermission.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determine if an exception indicates a network/connectivity issue
    /// (as opposed to a server-side error that we should not silently swallow).
    /// </summary>
    private static bool IsConnectivityException(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is OperationCanceledException
            || ex is System.Net.Sockets.SocketException;
    }
}

/// <summary>
/// Result of an edge authorization decision.
/// </summary>
public class EdgeAuthResult
{
    /// <summary>Whether the action is allowed.</summary>
    public bool Allowed { get; set; }

    /// <summary>Whether this decision came from the local cache rather than a live cloud call.</summary>
    public bool FromCache { get; set; }

    /// <summary>Whether the gateway is currently operating in offline mode.</summary>
    public bool OfflineMode { get; set; }

    /// <summary>Human-readable explanation for the decision.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Configuration options for the edge gateway, read from appsettings.json "IAM" section.
/// </summary>
public class EdgeGatewayOptions
{
    public string BaseUrl { get; set; } = "https://localhost:5161";
    public string GatewayDeviceId { get; set; } = "edge-gateway-001";
    public string SharedSecret { get; set; } = "configure-in-production";
    public int SyncIntervalMinutes { get; set; } = 5;
    public int HealthCheckIntervalSeconds { get; set; } = 60;
    public int CacheClaimsTtlMinutes { get; set; } = 15;
    public int CacheDecisionTtlMinutes { get; set; } = 5;
}

/// <summary>
/// DTO matching the JSON response from GET /api/device-auth/claims/{deviceId}.
/// </summary>
internal class DeviceClaimsResponse
{
    public string? DeviceId { get; set; }
    public string? DeviceType { get; set; }
    public string? ResourcePath { get; set; }
    public List<string>? Permissions { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public int CacheTtlSeconds { get; set; }
}
