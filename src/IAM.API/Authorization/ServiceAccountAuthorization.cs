using System.Security.Claims;

namespace IAM.API.Authorization;

/// <summary>
/// Recognizes service-account bearer tokens (client_credentials via
/// POST /api/service-accounts/token) inside role-gated controllers.
///
/// Service-account JWTs carry NO role claims — only "token_type=service_account",
/// "client_id" and one "permission" claim per granted permission (see
/// ServiceAccountService.GenerateServiceAccountToken). That means an
/// [Authorize(Roles = "...")] gate can never pass for a service account, even one
/// explicitly created for server-to-server provisioning (JengoWork task 1496:
/// TaskManager's one-button "Add Team Member" flow calls POST /api/users and
/// POST /api/invitations with its own scoped service credential).
///
/// This helper lets an endpoint opt in to a SPECIFIC permission instead of a role:
/// the service account must have been created with that exact permission string in
/// its Permissions list. No wildcard matching — a scoped credential stays scoped.
/// </summary>
public static class ServiceAccountAuthorization
{
    public const string TokenTypeClaim = "token_type";
    public const string ServiceAccountTokenType = "service_account";
    public const string PermissionClaim = "permission";

    /// <summary>Permission that allows POST /api/users (create active user with password).</summary>
    public const string UsersCreatePermission = "users:create";

    /// <summary>Permission that allows POST /api/invitations (send an invite email).</summary>
    public const string InvitationsSendPermission = "invitations:send";

    public static bool IsServiceAccount(ClaimsPrincipal principal) =>
        principal.HasClaim(TokenTypeClaim, ServiceAccountTokenType);

    /// <summary>True only for an authenticated service account whose token carries the exact permission.</summary>
    public static bool HasPermission(ClaimsPrincipal principal, string permission) =>
        IsServiceAccount(principal) && principal.HasClaim(PermissionClaim, permission);
}
