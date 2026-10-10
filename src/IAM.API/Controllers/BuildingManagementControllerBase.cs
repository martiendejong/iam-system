using System.Security.Claims;
using IAM.API.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// Shared authorization for the building-management API (locations, buildings, floors, rooms, room groups and IoT
/// devices, task 5164). It reuses <see cref="ITenantAccessResolver"/> (task 4726), so the rules are the same as for
/// /api/devices:
/// device, service-account and token-exchange tokens get 403 on every action; reading needs membership of the
/// tenant, changing needs SuperAdmin or a building-management role (TenantAdmin/BuildingOwner/BuildingManager) in it.
/// <para>
/// The tenant a call works in is never taken from a request body. It is the tenant_id claim when the token has one
/// (password-login tokens have none), else the optional <c>tenantId</c> query parameter, else the caller's only
/// tenant. A caller with several tenants (or a platform key / SuperAdmin without tenant claim) has to name the
/// tenant, and is refused with 403 for a tenant they do not belong to.
/// </para>
/// </summary>
[Authorize]
[ApiController]
public abstract class BuildingManagementControllerBase : ControllerBase
{
    private readonly ITenantAccessResolver _access;

    protected BuildingManagementControllerBase(ITenantAccessResolver access)
    {
        _access = access;
    }

    /// <summary>The tenant a request works in, or the response that refuses it.</summary>
    protected sealed class TenantScope
    {
        public Guid TenantId { get; init; }
        public ActionResult? Failure { get; init; }
    }

    /// <summary>Read access: any signed-in person who is a member of the tenant.</summary>
    protected Task<TenantScope> ReadScopeAsync(CancellationToken ct) => ResolveAsync(write: false, ct);

    /// <summary>Change access: SuperAdmin or a building-management role in the tenant.</summary>
    protected Task<TenantScope> ManageScopeAsync(CancellationToken ct) => ResolveAsync(write: true, ct);

    private async Task<TenantScope> ResolveAsync(bool write, CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
            return Fail(StatusCodes.Status403Forbidden,
                "Device, service-account and token-exchange tokens cannot use the building-management API.");

        Guid? tenantId = null;

        var claim = User.FindFirstValue("tenant_id");
        if (!string.IsNullOrWhiteSpace(claim))
        {
            if (!Guid.TryParse(claim, out var claimed))
                return Fail(StatusCodes.Status403Forbidden, "The token carries an invalid tenant.");
            tenantId = claimed;
        }

        var requested = Request.Query["tenantId"].ToString();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (!Guid.TryParse(requested, out var queried))
                return Fail(StatusCodes.Status400BadRequest, "tenantId must be a GUID.");
            if (tenantId.HasValue && tenantId.Value != queried)
                return Fail(StatusCodes.Status403Forbidden, "This token is limited to another tenant.");
            tenantId = queried;
        }

        if (!tenantId.HasValue)
        {
            var readable = access.ReadableTenants;
            if (readable is { Count: 0 })
                return Fail(StatusCodes.Status403Forbidden, "You do not belong to any tenant.");
            else if (readable is { Count: 1 })
                tenantId = readable.Single();
            else
                return Fail(StatusCodes.Status400BadRequest, "Pass the tenant to work in as the tenantId query parameter.");
        }

        if (write ? !access.CanManage(tenantId.Value) : !access.CanRead(tenantId.Value))
            return Fail(StatusCodes.Status403Forbidden, write
                ? "Only SuperAdmin or a building owner/manager of this tenant can change building-management data."
                : "You do not have access to the building-management data of this tenant.");

        return new TenantScope { TenantId = tenantId.Value };
    }

    private TenantScope Fail(int status, string error) =>
        new() { Failure = StatusCode(status, new { error }) };
}
