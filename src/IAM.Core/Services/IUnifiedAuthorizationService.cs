namespace IAM.Core.Services;

/// <summary>
/// Unified authorization engine that combines user and device authorization
/// into a single evaluation pipeline. Delegates to PolicyInheritanceEngine for users
/// and uses hierarchical permission matching for devices.
/// </summary>
public interface IUnifiedAuthorizationService
{
    /// <summary>
    /// Evaluate access for any principal type (user or device).
    /// For users: delegates to PolicyInheritanceEngine with group permissions.
    /// For devices: uses hierarchical permission matching from device permissions JSON.
    /// </summary>
    Task<AuthorizationDecision> EvaluateAsync(AuthorizationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Batch evaluate multiple resources at once.
    /// Processes all requests in parallel for optimal performance.
    /// </summary>
    Task<List<AuthorizationDecision>> EvaluateBatchAsync(List<AuthorizationRequest> requests, CancellationToken ct = default);

    /// <summary>
    /// Get all effective permissions for a principal.
    /// For users: direct role permissions + inherited tenant permissions + group permissions - denied.
    /// For devices: permissions from Device.Permissions JSON.
    /// </summary>
    Task<EffectivePermissions> GetEffectivePermissionsAsync(string principalType, Guid principalId, Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Request model for a unified authorization check.
/// </summary>
public class AuthorizationRequest
{
    /// <summary>
    /// Type of principal: "user" or "device"
    /// </summary>
    public string PrincipalType { get; set; } = "user";

    /// <summary>
    /// The principal's unique identifier.
    /// For users: User.Id (Guid). For devices: Device.Id (Guid).
    /// </summary>
    public Guid PrincipalId { get; set; }

    /// <summary>
    /// Tenant context for the authorization check (spatial scope).
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Resource being accessed (e.g., "Door:Unlock", "acme:hq:floor-3:sensor:temp-089").
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// Action being performed on the resource (e.g., "View", "Control", "read", "write").
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Additional context for ABAC conditions (IP address, device health, location, etc.).
    /// </summary>
    public Dictionary<string, object>? Context { get; set; }
}

/// <summary>
/// Result of a unified authorization evaluation.
/// </summary>
public class AuthorizationDecision
{
    /// <summary>
    /// Whether access is granted.
    /// </summary>
    public bool Allowed { get; set; }

    /// <summary>
    /// Human-readable reason for the decision (for audit logs and debugging).
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Name of the policy that matched (for user principals), if any.
    /// </summary>
    public string? MatchedPolicy { get; set; }

    /// <summary>
    /// Permission string that matched (for device principals), if any.
    /// </summary>
    public string? MatchedPermission { get; set; }

    /// <summary>
    /// Type of principal that was evaluated ("user" or "device").
    /// </summary>
    public string PrincipalType { get; set; } = string.Empty;

    /// <summary>
    /// Time taken to evaluate the authorization request, in milliseconds.
    /// </summary>
    public long EvaluationTimeMs { get; set; }

    /// <summary>
    /// Debug trace showing the evaluation path (steps taken during evaluation).
    /// </summary>
    public List<string> EvaluationPath { get; set; } = new();
}

/// <summary>
/// Aggregated effective permissions for a principal, showing all sources of permissions
/// and the final merged result after deny overrides.
/// </summary>
public class EffectivePermissions
{
    /// <summary>
    /// Type of principal ("user" or "device").
    /// </summary>
    public string PrincipalType { get; set; } = string.Empty;

    /// <summary>
    /// The principal's unique identifier.
    /// </summary>
    public Guid PrincipalId { get; set; }

    /// <summary>
    /// Permissions from direct role assignments (user) or device permissions JSON (device).
    /// </summary>
    public List<string> DirectPermissions { get; set; } = new();

    /// <summary>
    /// Permissions inherited from parent tenants in the hierarchy.
    /// </summary>
    public List<string> InheritedPermissions { get; set; } = new();

    /// <summary>
    /// Permissions obtained through group membership and group role assignments.
    /// </summary>
    public List<string> GroupPermissions { get; set; } = new();

    /// <summary>
    /// Permissions explicitly denied by Deny policies.
    /// </summary>
    public List<string> DeniedPermissions { get; set; } = new();

    /// <summary>
    /// Final merged list of allowed permissions (all allowed - denied).
    /// </summary>
    public List<string> EffectiveAllowed { get; set; } = new();
}
