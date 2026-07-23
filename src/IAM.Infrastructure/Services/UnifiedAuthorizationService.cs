using System.Diagnostics;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Unified authorization engine that combines user-based policy evaluation
/// and device-based hierarchical permission matching into a single service.
///
/// For users: delegates to IPolicyInheritanceEngine for policy-based evaluation,
/// enriched with group role permissions.
///
/// For devices: queries Device.Permissions JSON and performs hierarchical matching
/// with wildcard and superpath support.
///
/// Includes caching for frequently evaluated requests and Stopwatch-based timing.
/// </summary>
public class UnifiedAuthorizationService : IUnifiedAuthorizationService
{
    private readonly IAMDbContext _context;
    private readonly IPolicyInheritanceEngine _policyEngine;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UnifiedAuthorizationService> _logger;

    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(2);
    private const string CachePrefix = "unified_authz_";

    public UnifiedAuthorizationService(
        IAMDbContext context,
        IPolicyInheritanceEngine policyEngine,
        IMemoryCache cache,
        ILogger<UnifiedAuthorizationService> logger)
    {
        _context = context;
        _policyEngine = policyEngine;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AuthorizationDecision> EvaluateAsync(
        AuthorizationRequest request,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var evaluationPath = new List<string>();

        try
        {
            // Validate request
            if (request.PrincipalId == Guid.Empty)
            {
                return BuildDecision(false, "Principal ID is required", request.PrincipalType,
                    stopwatch, evaluationPath);
            }

            if (string.IsNullOrWhiteSpace(request.Resource))
            {
                return BuildDecision(false, "Resource is required", request.PrincipalType,
                    stopwatch, evaluationPath);
            }

            evaluationPath.Add($"Evaluating {request.PrincipalType} principal {request.PrincipalId}");
            evaluationPath.Add($"Resource: {request.Resource}, Action: {request.Action}");

            // Check cache
            var cacheKey = BuildCacheKey(request);
            if (_cache.TryGetValue(cacheKey, out AuthorizationDecision? cached) && cached != null)
            {
                evaluationPath.Add("Cache hit - returning cached decision");
                cached.EvaluationPath = evaluationPath;
                cached.EvaluationTimeMs = stopwatch.ElapsedMilliseconds;
                return cached;
            }

            evaluationPath.Add("Cache miss - performing evaluation");

            AuthorizationDecision decision;

            switch (request.PrincipalType.ToLowerInvariant())
            {
                case "user":
                    decision = await EvaluateUserAsync(request, evaluationPath, ct);
                    break;

                case "device":
                    decision = await EvaluateDeviceAsync(request, evaluationPath, ct);
                    break;

                default:
                    decision = BuildDecision(false,
                        $"Unknown principal type '{request.PrincipalType}'. Supported: user, device",
                        request.PrincipalType, stopwatch, evaluationPath);
                    return decision;
            }

            stopwatch.Stop();
            decision.EvaluationTimeMs = stopwatch.ElapsedMilliseconds;
            decision.EvaluationPath = evaluationPath;

            // Cache the result
            _cache.Set(cacheKey, decision, CacheExpiration);

            _logger.LogInformation(
                "Authorization evaluated: {PrincipalType}={PrincipalId}, Resource={Resource}, Action={Action}, Allowed={Allowed} in {Ms}ms",
                request.PrincipalType, request.PrincipalId, request.Resource, request.Action,
                decision.Allowed, decision.EvaluationTimeMs);

            return decision;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Authorization evaluation failed: {PrincipalType}={PrincipalId}, Resource={Resource}",
                request.PrincipalType, request.PrincipalId, request.Resource);

            stopwatch.Stop();
            evaluationPath.Add($"Exception: {ex.Message}");

            return BuildDecision(false, $"Evaluation error: {ex.Message}",
                request.PrincipalType, stopwatch, evaluationPath);
        }
    }

    /// <inheritdoc />
    public async Task<List<AuthorizationDecision>> EvaluateBatchAsync(
        List<AuthorizationRequest> requests,
        CancellationToken ct = default)
    {
        if (requests == null || requests.Count == 0)
            return new List<AuthorizationDecision>();

        _logger.LogInformation("Batch authorization evaluation: {Count} requests", requests.Count);

        // Process all requests in parallel
        var tasks = requests.Select(r => EvaluateAsync(r, ct));
        var results = await Task.WhenAll(tasks);

        return results.ToList();
    }

    /// <inheritdoc />
    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(
        string principalType,
        Guid principalId,
        Guid tenantId,
        CancellationToken ct = default)
    {
        return principalType.ToLowerInvariant() switch
        {
            "user" => await GetUserEffectivePermissionsAsync(principalId, tenantId, ct),
            "device" => await GetDeviceEffectivePermissionsAsync(principalId, ct),
            _ => new EffectivePermissions
            {
                PrincipalType = principalType,
                PrincipalId = principalId
            }
        };
    }

    #region User Evaluation

    /// <summary>
    /// Evaluates authorization for a user principal by delegating to the PolicyInheritanceEngine
    /// and additionally checking group-based permissions.
    /// </summary>
    private async Task<AuthorizationDecision> EvaluateUserAsync(
        AuthorizationRequest request,
        List<string> evaluationPath,
        CancellationToken ct)
    {
        evaluationPath.Add("Evaluating user via PolicyInheritanceEngine");

        // Build evaluation context from the request's extra context
        var evalContext = new PolicyEvaluationContext
        {
            EvaluationTime = DateTime.UtcNow
        };

        if (request.Context != null)
        {
            if (request.Context.TryGetValue("ip_address", out var ip))
                evalContext.IpAddress = ip?.ToString();
            if (request.Context.TryGetValue("device_id", out var deviceId))
                evalContext.DeviceId = deviceId?.ToString();
            if (request.Context.TryGetValue("device_health", out var health))
                evalContext.DeviceHealth = health?.ToString();
            if (request.Context.TryGetValue("location", out var location))
                evalContext.Location = location?.ToString();

            evalContext.CustomAttributes = request.Context;
        }

        // Delegate to the existing PolicyInheritanceEngine
        var policyResult = await _policyEngine.EvaluateAsync(
            request.PrincipalId,
            request.TenantId,
            request.Resource,
            request.Action,
            evalContext,
            ct);

        evaluationPath.Add($"PolicyEngine result: Allowed={policyResult.IsAllowed}, " +
                           $"EvaluatedPolicies={policyResult.EvaluatedPoliciesCount}, " +
                           $"Reason={policyResult.Reason}");

        // If the policy engine gave a definitive answer (explicit allow or deny), return it
        if (policyResult.MatchedPolicy != null)
        {
            return new AuthorizationDecision
            {
                Allowed = policyResult.IsAllowed,
                Reason = policyResult.Reason,
                MatchedPolicy = policyResult.MatchedPolicy.Name,
                PrincipalType = "user"
            };
        }

        // No matching policy found via direct role policies - check group-based permissions
        evaluationPath.Add("No direct policy match, checking group permissions");

        var groupDecision = await EvaluateUserGroupPermissionsAsync(
            request.PrincipalId, request.TenantId, request.Resource, request.Action,
            evaluationPath, ct);

        if (groupDecision != null)
        {
            return groupDecision;
        }

        evaluationPath.Add("No group permissions matched - default deny");

        // Default deny
        return new AuthorizationDecision
        {
            Allowed = false,
            Reason = "No matching policy or group permission found (default deny)",
            PrincipalType = "user"
        };
    }

    /// <summary>
    /// Checks if the user has access through group membership.
    /// Queries the user's active group memberships, then checks GroupRoles for each group
    /// in the given tenant, and evaluates whether those roles' permissions cover the request.
    /// </summary>
    private async Task<AuthorizationDecision?> EvaluateUserGroupPermissionsAsync(
        Guid userId,
        Guid tenantId,
        string resource,
        string action,
        List<string> evaluationPath,
        CancellationToken ct)
    {
        // Get active group memberships for this user
        var groupIds = await _context.GroupMemberships
            .Where(gm => gm.UserId == userId
                         && gm.IsActive
                         && (gm.ExpiresAt == null || gm.ExpiresAt > DateTime.UtcNow))
            .Select(gm => gm.GroupId)
            .ToListAsync(ct);

        if (groupIds.Count == 0)
        {
            evaluationPath.Add("User has no active group memberships");
            return null;
        }

        evaluationPath.Add($"User belongs to {groupIds.Count} active group(s)");

        // Get roles assigned to these groups within the tenant context
        var groupRoles = await _context.GroupRoles
            .Include(gr => gr.Role)
            .Where(gr => groupIds.Contains(gr.GroupId) && gr.TenantId == tenantId)
            .ToListAsync(ct);

        if (groupRoles.Count == 0)
        {
            evaluationPath.Add("No group roles found for user's groups in this tenant");
            return null;
        }

        evaluationPath.Add($"Found {groupRoles.Count} group role assignment(s) in tenant");

        // Check if any group role has a permission that matches the requested resource:action
        foreach (var groupRole in groupRoles)
        {
            var rolePermissions = DeserializePermissions(groupRole.Role?.Permissions ?? "[]");

            var requestedPermission = string.IsNullOrWhiteSpace(action)
                ? resource
                : $"{resource}:{action}";

            foreach (var permission in rolePermissions)
            {
                if (IsHierarchicalMatch(permission, requestedPermission))
                {
                    evaluationPath.Add(
                        $"Group role '{groupRole.Role?.Name}' grants permission '{permission}' " +
                        $"matching '{requestedPermission}'");

                    return new AuthorizationDecision
                    {
                        Allowed = true,
                        Reason = $"Allowed by group role '{groupRole.Role?.Name}' " +
                                 $"(permission: {permission})",
                        MatchedPermission = permission,
                        PrincipalType = "user"
                    };
                }
            }
        }

        evaluationPath.Add("No matching group role permissions found");
        return null;
    }

    /// <summary>
    /// Builds the effective permissions for a user, aggregating direct roles,
    /// inherited tenant roles, and group roles, then subtracting denied permissions.
    /// </summary>
    private async Task<EffectivePermissions> GetUserEffectivePermissionsAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct)
    {
        var result = new EffectivePermissions
        {
            PrincipalType = "user",
            PrincipalId = userId
        };

        // 1. Direct role permissions: roles assigned directly to the user in this tenant
        var directRoles = await _context.UserRoles
            .Include(ur => ur.Role)
            .Where(ur => ur.UserId == userId
                         && ur.TenantId == tenantId
                         && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .ToListAsync(ct);

        foreach (var ur in directRoles)
        {
            var perms = DeserializePermissions(ur.Role?.Permissions ?? "[]");
            result.DirectPermissions.AddRange(perms);
        }

        // 2. Inherited permissions: from parent tenant roles via the policy engine
        var effectivePolicies = await _policyEngine.GetEffectivePoliciesForTenantAsync(tenantId, ct);

        // Collect Allow permissions from inherited policies that match this user
        var userRoleIds = directRoles.Select(ur => ur.RoleId).ToList();

        // Also include roles from parent tenants
        var parentRoles = await _context.UserRoles
            .Include(ur => ur.Role)
            .Where(ur => ur.UserId == userId
                         && ur.TenantId != tenantId
                         && ur.TenantId != null
                         && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .ToListAsync(ct);

        var allRoleIds = userRoleIds
            .Concat(parentRoles.Select(ur => ur.RoleId))
            .Distinct()
            .ToList();

        foreach (var policy in effectivePolicies)
        {
            // Check if this policy applies to this user
            var appliesToUser = false;

            if (policy.UserId.HasValue && policy.UserId.Value == userId)
                appliesToUser = true;
            else if (policy.RoleId.HasValue && allRoleIds.Contains(policy.RoleId.Value))
                appliesToUser = true;
            else if (!policy.UserId.HasValue && !policy.RoleId.HasValue)
                appliesToUser = true; // Policy applies to everyone

            if (!appliesToUser) continue;

            var permString = $"{policy.Resource}:{policy.Action}";

            if (policy.Effect == PolicyEffect.Allow)
            {
                // Only count as inherited if from a different tenant
                if (policy.TenantId != tenantId)
                    result.InheritedPermissions.Add(permString);
            }
            else if (policy.Effect == PolicyEffect.Deny)
            {
                result.DeniedPermissions.Add(permString);
            }
        }

        // 3. Group permissions: roles assigned to groups the user belongs to
        var groupIds = await _context.GroupMemberships
            .Where(gm => gm.UserId == userId
                         && gm.IsActive
                         && (gm.ExpiresAt == null || gm.ExpiresAt > DateTime.UtcNow))
            .Select(gm => gm.GroupId)
            .ToListAsync(ct);

        if (groupIds.Count > 0)
        {
            var groupRoles = await _context.GroupRoles
                .Include(gr => gr.Role)
                .Where(gr => groupIds.Contains(gr.GroupId) && gr.TenantId == tenantId)
                .ToListAsync(ct);

            foreach (var gr in groupRoles)
            {
                var perms = DeserializePermissions(gr.Role?.Permissions ?? "[]");
                result.GroupPermissions.AddRange(perms);
            }
        }

        // 4. Compute effective allowed: (direct + inherited + group) - denied
        var allAllowed = result.DirectPermissions
            .Concat(result.InheritedPermissions)
            .Concat(result.GroupPermissions)
            .Distinct()
            .ToList();

        result.EffectiveAllowed = allAllowed
            .Where(perm => !result.DeniedPermissions.Any(denied =>
                IsHierarchicalMatch(denied, perm)))
            .Distinct()
            .ToList();

        // Deduplicate all lists
        result.DirectPermissions = result.DirectPermissions.Distinct().ToList();
        result.InheritedPermissions = result.InheritedPermissions.Distinct().ToList();
        result.GroupPermissions = result.GroupPermissions.Distinct().ToList();
        result.DeniedPermissions = result.DeniedPermissions.Distinct().ToList();

        return result;
    }

    #endregion

    #region Device Evaluation

    /// <summary>
    /// Evaluates authorization for a device principal by looking up the device
    /// and checking its permissions JSON using hierarchical matching.
    /// </summary>
    private async Task<AuthorizationDecision> EvaluateDeviceAsync(
        AuthorizationRequest request,
        List<string> evaluationPath,
        CancellationToken ct)
    {
        evaluationPath.Add("Evaluating device via hierarchical permission matching");

        // Look up device by its Guid Id
        var device = await _context.Devices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == request.PrincipalId && d.IsActive, ct);

        if (device == null)
        {
            evaluationPath.Add($"Device {request.PrincipalId} not found or inactive");
            return new AuthorizationDecision
            {
                Allowed = false,
                Reason = "Device not found or inactive",
                PrincipalType = "device"
            };
        }

        evaluationPath.Add($"Found device '{device.Name}' ({device.DeviceId}), type={device.DeviceType}");

        // Verify tenant context matches (device must belong to the requested tenant)
        if (device.TenantId != request.TenantId)
        {
            evaluationPath.Add($"Device tenant {device.TenantId} does not match requested tenant {request.TenantId}");
            return new AuthorizationDecision
            {
                Allowed = false,
                Reason = "Device does not belong to the specified tenant",
                PrincipalType = "device"
            };
        }

        var permissions = DeserializePermissions(device.Permissions);
        evaluationPath.Add($"Device has {permissions.Count} permission(s)");

        var requestedPermission = string.IsNullOrWhiteSpace(request.Action)
            ? request.Resource
            : $"{request.Resource}:{request.Action}";

        evaluationPath.Add($"Checking hierarchical match for '{requestedPermission}'");

        // Check hierarchical permission match
        foreach (var permission in permissions)
        {
            if (IsHierarchicalMatch(permission, requestedPermission))
            {
                evaluationPath.Add($"Match found: permission '{permission}' covers '{requestedPermission}'");

                return new AuthorizationDecision
                {
                    Allowed = true,
                    Reason = $"Device permission '{permission}' matches requested '{requestedPermission}'",
                    MatchedPermission = permission,
                    PrincipalType = "device"
                };
            }
        }

        evaluationPath.Add("No matching device permission found");

        return new AuthorizationDecision
        {
            Allowed = false,
            Reason = $"No device permission found for '{requestedPermission}'",
            PrincipalType = "device"
        };
    }

    /// <summary>
    /// Builds the effective permissions for a device from its permissions JSON.
    /// Devices have a flat permission model (no inheritance or groups).
    /// </summary>
    private async Task<EffectivePermissions> GetDeviceEffectivePermissionsAsync(
        Guid deviceId,
        CancellationToken ct)
    {
        var result = new EffectivePermissions
        {
            PrincipalType = "device",
            PrincipalId = deviceId
        };

        var device = await _context.Devices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.IsActive, ct);

        if (device == null)
            return result;

        var permissions = DeserializePermissions(device.Permissions);

        // All device permissions are direct (no inheritance or group model for devices)
        result.DirectPermissions = permissions;
        result.EffectiveAllowed = permissions;

        return result;
    }

    #endregion

    #region Shared Helper Methods

    /// <summary>
    /// Hierarchical permission matching with wildcard support.
    /// Reuses the same logic as DeviceAuthenticationService.IsHierarchicalMatch.
    ///
    /// "acme:hq:floor-3:sensor:*" matches "acme:hq:floor-3:sensor:temp-089"
    /// "acme:hq:*" matches "acme:hq:floor-3:hvac:unit-247:telemetry:write"
    /// "Door:*" matches "Door:Unlock"
    /// Superpath: "acme:hq:floor-3" implicitly allows "acme:hq:floor-3:sensor:temp:read"
    /// </summary>
    private static bool IsHierarchicalMatch(string permission, string requested)
    {
        // Exact match
        if (permission.Equals(requested, StringComparison.OrdinalIgnoreCase))
            return true;

        // Wildcard at end: "acme:hq:*" matches everything under "acme:hq:"
        if (permission.EndsWith(":*"))
        {
            var prefix = permission[..^2]; // Remove ":*"
            return requested.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)
                   || requested.Equals(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Full wildcard
        if (permission == "*")
            return true;

        // Superpath match: "acme:hq:floor-3" implicitly allows "acme:hq:floor-3:sensor:temp:read"
        if (requested.StartsWith(permission + ":", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Deserializes a JSON array of permission strings.
    /// Returns empty list on null, empty, or invalid JSON.
    /// </summary>
    private static List<string> DeserializePermissions(string permissionsJson)
    {
        if (string.IsNullOrWhiteSpace(permissionsJson))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(permissionsJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Builds a cache key from the authorization request.
    /// </summary>
    private static string BuildCacheKey(AuthorizationRequest request)
    {
        return $"{CachePrefix}{request.PrincipalType}_{request.PrincipalId}_{request.TenantId}_{request.Resource}_{request.Action}";
    }

    /// <summary>
    /// Builds an AuthorizationDecision with timing and path info.
    /// </summary>
    private static AuthorizationDecision BuildDecision(
        bool allowed,
        string reason,
        string principalType,
        Stopwatch stopwatch,
        List<string> evaluationPath)
    {
        stopwatch.Stop();
        return new AuthorizationDecision
        {
            Allowed = allowed,
            Reason = reason,
            PrincipalType = principalType,
            EvaluationTimeMs = stopwatch.ElapsedMilliseconds,
            EvaluationPath = evaluationPath
        };
    }

    #endregion
}
