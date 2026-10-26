using System.Security.Claims;
using Hazina.Security.ApiKeys;

namespace IAM.API.Authorization;

/// <summary>What a caller may do with one "evaluate principal X in tenant T" question (task 5162).</summary>
public enum EvaluationAccess
{
    /// <summary>The caller may not ask about this principal in this tenant.</summary>
    Denied,

    /// <summary>The caller asks about itself: the answer, but no policy names, evaluation path or matched permission.</summary>
    Self,

    /// <summary>Administrator of the tenant, or a permitted service account: the full answer with the evaluation detail.</summary>
    Admin
}

/// <summary>
/// The caller of the /api/authorize endpoints, built once per request from the token only (never from the request
/// body) and asked <see cref="Check"/> for every principal/tenant pair the request names, so a batch is judged item
/// by item against one identity.
/// </summary>
public sealed class EvaluationCaller
{
    private readonly TenantAccess? _tenants;
    private readonly bool _serviceAccountMayEvaluate;
    private readonly Guid? _pinnedTenantId;
    private readonly Guid? _selfUserId;
    private readonly Guid? _selfDeviceId;

    private EvaluationCaller(
        TenantAccess? tenants,
        bool serviceAccountMayEvaluate,
        Guid? pinnedTenantId,
        Guid? selfUserId,
        Guid? selfDeviceId)
    {
        _tenants = tenants;
        _serviceAccountMayEvaluate = serviceAccountMayEvaluate;
        _pinnedTenantId = pinnedTenantId;
        _selfUserId = selfUserId;
        _selfDeviceId = selfDeviceId;
    }

    public static EvaluationCaller Refused() => new(null, false, null, null, null);

    /// <summary>A service account holding authorize:evaluate; a tenant_id claim on it pins it to that tenant.</summary>
    public static EvaluationCaller ServiceAccount(Guid? pinnedTenantId) => new(null, true, pinnedTenantId, null, null);

    public static EvaluationCaller Principal(TenantAccess tenants, Guid? selfUserId, Guid? selfDeviceId) =>
        new(tenants, false, null, selfUserId, selfDeviceId);

    public EvaluationAccess Check(string? principalType, Guid principalId, Guid tenantId)
    {
        // Administering the queried tenant beats being the principal: an admin asking about themselves still
        // gets the full detail.
        if (_serviceAccountMayEvaluate)
            return !_pinnedTenantId.HasValue || _pinnedTenantId.Value == tenantId
                ? EvaluationAccess.Admin
                : EvaluationAccess.Denied;

        if (_tenants != null && _tenants.CanManage(tenantId))
            return EvaluationAccess.Admin;

        if (principalId != Guid.Empty)
        {
            if (string.Equals(principalType, "user", StringComparison.OrdinalIgnoreCase) && principalId == _selfUserId)
                return EvaluationAccess.Self;

            if (string.Equals(principalType, "device", StringComparison.OrdinalIgnoreCase) && principalId == _selfDeviceId)
                return EvaluationAccess.Self;
        }

        return EvaluationAccess.Denied;
    }
}

/// <summary>
/// Decides who may ask the /api/authorize endpoints about whom (task 5162). Permission lists, matched policies and
/// the evaluation path describe how a tenant is set up, so the question about another principal is an
/// administrator's: the tenant's admin (<see cref="ITenantAccessResolver"/>, the one tenant-admin rule), or a
/// service account holding the exact <see cref="ServiceAccountAuthorization.AuthorizeEvaluatePermission"/>. Every
/// other caller may only ask about itself.
/// </summary>
public interface IEvaluationAccessResolver
{
    Task<EvaluationCaller> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

public class EvaluationAccessResolver : IEvaluationAccessResolver
{
    private const string DeviceTokenType = "device";

    private readonly ITenantAccessResolver _tenantAccess;

    public EvaluationAccessResolver(ITenantAccessResolver tenantAccess)
    {
        _tenantAccess = tenantAccess;
    }

    public async Task<EvaluationCaller> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        // API keys first (the same order TenantAccessResolver uses): a key issued by a user carries that user's id
        // as NameIdentifier, but it is a credential of its own, so it never counts as "the user asking about
        // themselves". It only gets the tenant-admin route (admin scope on its tenant, or a platform admin key).
        if (user.IsApiKey())
            return EvaluationCaller.Principal(await _tenantAccess.ResolveAsync(user, cancellationToken), null, null);

        // Service accounts: only with the exact permission claim (no wildcard matching), and a tenant_id claim keeps
        // the credential inside its tenant. Without the permission they are a plain refused caller; there is no
        // "self" for a service account.
        if (ServiceAccountAuthorization.IsServiceAccount(user))
        {
            if (!ServiceAccountAuthorization.HasPermission(user, ServiceAccountAuthorization.AuthorizeEvaluatePermission))
                return EvaluationCaller.Refused();

            var tenantClaim = user.FindFirst("tenant_id")?.Value;
            if (string.IsNullOrWhiteSpace(tenantClaim))
                return EvaluationCaller.ServiceAccount(null);

            // A tenant claim that is not a tenant id pins nothing, so it refuses everything.
            return Guid.TryParse(tenantClaim, out var pinned)
                ? EvaluationCaller.ServiceAccount(pinned)
                : EvaluationCaller.Refused();
        }

        var subject = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var subjectId)
            ? subjectId
            : (Guid?)null;

        var tokenType = user.FindFirstValue(ServiceAccountAuthorization.TokenTypeClaim);

        // A device token's sub is the device's own id, so a device may ask about itself. Any other token type
        // (token exchange, ...) is neither a user nor a device and has no "self".
        if (tokenType != null)
        {
            var selfDevice = string.Equals(tokenType, DeviceTokenType, StringComparison.Ordinal) ? subject : null;
            return EvaluationCaller.Principal(
                await _tenantAccess.ResolveAsync(user, cancellationToken), null, selfDevice);
        }

        return EvaluationCaller.Principal(await _tenantAccess.ResolveAsync(user, cancellationToken), subject, null);
    }
}
