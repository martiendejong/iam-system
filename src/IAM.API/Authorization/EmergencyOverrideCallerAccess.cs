using IAM.Core.Services;

namespace IAM.API.Authorization;

/// <summary>
/// Task 5632: answers "may the caller of this request manage tenant X" for the emergency override service by reusing
/// <see cref="ITenantAccessResolver"/>. SuperAdmin and platform admin keys count as managing every tenant, so
/// break-glass can stay cross-tenant for platform administrators.
/// </summary>
public class EmergencyOverrideCallerAccess : IEmergencyOverrideCallerAccess
{
    private readonly IHttpContextAccessor _http;
    private readonly ITenantAccessResolver _resolver;

    public EmergencyOverrideCallerAccess(IHttpContextAccessor http, ITenantAccessResolver resolver)
    {
        _http = http;
        _resolver = resolver;
    }

    public async Task<bool> CanManageTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var user = _http.HttpContext?.User;
        if (user == null)
            return false;

        var access = await _resolver.ResolveAsync(user, cancellationToken);
        return access.CanManage(tenantId);
    }
}
