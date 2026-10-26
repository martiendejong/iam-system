using System.Text.RegularExpressions;

namespace IAM.Core.Services;

/// <summary>
/// Input rules for newly issued API keys (task 4701). The service enforces them, so no caller can skip them.
/// </summary>
public static partial class ApiKeyIssueRules
{
    /// <summary>Highest rate limit a key may be issued with (the platform default is 300/min).</summary>
    public const int MaxRateLimitPerMinute = 1000;

    public const int MaxPermissions = 25;

    // "resource:action", lowercase, no wildcards (users:create, invitations:send).
    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,63}:[a-z0-9_.-]{1,64}$")]
    private static partial Regex PermissionShape();

    /// <summary>Role name that makes a user an admin of the tenant its UserRole row is scoped to.</summary>
    public const string TenantAdminRoleName = "TenantAdmin";

    /// <summary>Returns an error message, or null when the permissions and rate limit are acceptable.</summary>
    public static string? Validate(IReadOnlyCollection<string>? permissions, int? rateLimitPerMinute)
    {
        if (permissions != null)
        {
            if (permissions.Count > MaxPermissions)
                return $"At most {MaxPermissions} permissions are allowed.";

            foreach (var permission in permissions)
            {
                if (string.IsNullOrWhiteSpace(permission) || !PermissionShape().IsMatch(permission))
                    return $"Invalid permission '{permission}': use lowercase 'resource:action' (no wildcards).";
            }
        }

        if (rateLimitPerMinute is { } limit && (limit < 1 || limit > MaxRateLimitPerMinute))
            return $"rateLimitPerMinute must be between 1 and {MaxRateLimitPerMinute}.";

        return null;
    }
}
