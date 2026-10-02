using System.Security.Claims;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Authorization;

/// <summary>What a caller wants to do with a device's telemetry.</summary>
public enum TelemetryAction
{
    /// <summary>Subscribe to or read telemetry.</summary>
    Read,

    /// <summary>Publish telemetry or report device status.</summary>
    Write,

    /// <summary>Send a command to the device. Users only; a device never commands.</summary>
    Command
}

/// <summary>The registry facts of a device. Tenant and type always come from here, never from a message.</summary>
public sealed record TelemetryDevice(string DeviceId, string DeviceType, Guid TenantId, bool IsActive);

/// <summary>Outcome of <see cref="ITelemetryAccessAuthorizer.AuthorizeDeviceAsync"/>.</summary>
public sealed record TelemetryDeviceAccess(bool Allowed, TelemetryDevice? Device, string? Message)
{
    public static TelemetryDeviceAccess Allow(TelemetryDevice device) => new(true, device, null);
    public static TelemetryDeviceAccess Deny(string message) => new(false, null, message);
}

/// <summary>Outcome of <see cref="ITelemetryAccessAuthorizer.AuthorizeTenantReadAsync"/>.</summary>
public sealed record TelemetryTenantAccess(bool Allowed, string? Message)
{
    public static TelemetryTenantAccess Allow() => new(true, null);
    public static TelemetryTenantAccess Deny(string message) => new(false, message);
}

public enum TelemetryScopeDecision
{
    Allowed,
    Forbidden,
    TenantRequired
}

/// <summary>
/// Outcome of <see cref="ITelemetryAccessAuthorizer.ResolveReadScopeAsync"/>. When allowed,
/// <see cref="TenantId"/> is the tenant a query must be pinned to; null (SuperAdmin only) means all tenants.
/// </summary>
public sealed record TelemetryReadScope(TelemetryScopeDecision Decision, Guid? TenantId, string? Message)
{
    public static TelemetryReadScope Allow(Guid? tenantId) => new(TelemetryScopeDecision.Allowed, tenantId, null);
    public static TelemetryReadScope Forbid(string message) => new(TelemetryScopeDecision.Forbidden, null, message);
    public static TelemetryReadScope NeedTenant(string message) => new(TelemetryScopeDecision.TenantRequired, null, message);
}

/// <summary>
/// The one authorizer behind the telemetry SignalR hub and the REST telemetry API (task 4708): both
/// expose the same data, so both ask the same questions of the caller.
/// </summary>
public interface ITelemetryAccessAuthorizer
{
    /// <summary>
    /// May the caller read, write or command this device? The device must exist in the registry.
    /// A device token acts only for itself (read/write, never command); a user needs membership of the
    /// device's tenant to read, and SuperAdmin/BuildingOwner/BuildingManager of it to write or command.
    /// </summary>
    Task<TelemetryDeviceAccess> AuthorizeDeviceAsync(
        ClaimsPrincipal caller, string? deviceId, TelemetryAction action, CancellationToken cancellationToken);

    /// <summary>May the caller subscribe to the whole telemetry stream of this tenant? Users only.</summary>
    Task<TelemetryTenantAccess> AuthorizeTenantReadAsync(
        ClaimsPrincipal caller, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Which tenant a tenant-wide read (query, export, statistics, device-type stream) is pinned to.</summary>
    Task<TelemetryReadScope> ResolveReadScopeAsync(
        ClaimsPrincipal caller, Guid? requestedTenantId, CancellationToken cancellationToken);
}

public class TelemetryAccessAuthorizer : ITelemetryAccessAuthorizer
{
    private const string PlatformRole = "SuperAdmin";
    private const string DeniedDevice = "You do not have access to this device.";
    private const string DeniedTenant = "You do not have access to this tenant.";

    /// <summary>Tenant roles that may publish, report status and send commands (as in TenantsController).</summary>
    internal static readonly string[] WriteRoles = { "BuildingOwner", "BuildingManager" };

    private readonly IAMDbContext _context;

    public TelemetryAccessAuthorizer(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<TelemetryDeviceAccess> AuthorizeDeviceAsync(
        ClaimsPrincipal caller, string? deviceId, TelemetryAction action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return TelemetryDeviceAccess.Deny(DeniedDevice);

        var tokenType = caller.FindFirst(ServiceAccountAuthorization.TokenTypeClaim)?.Value;
        if (tokenType != null && tokenType != "device")
            return TelemetryDeviceAccess.Deny(DeniedDevice);

        var device = await _context.Devices
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId)
            .Select(d => new TelemetryDevice(d.DeviceId, d.DeviceType, d.TenantId, d.IsActive))
            .FirstOrDefaultAsync(cancellationToken);
        if (device == null)
            return TelemetryDeviceAccess.Deny(DeniedDevice);

        if (tokenType == "device")
        {
            // A device acts for itself only, and never commands (itself or anyone else).
            if (action == TelemetryAction.Command || !device.IsActive)
                return TelemetryDeviceAccess.Deny(DeniedDevice);

            var tokenDeviceId = caller.FindFirst("device_id")?.Value;
            if (!string.Equals(tokenDeviceId, device.DeviceId, StringComparison.Ordinal))
                return TelemetryDeviceAccess.Deny(DeniedDevice);

            var tokenTenant = caller.FindFirst("tenant_id")?.Value;
            if (!string.IsNullOrWhiteSpace(tokenTenant)
                && (!Guid.TryParse(tokenTenant, out var tokenTenantId) || tokenTenantId != device.TenantId))
                return TelemetryDeviceAccess.Deny(DeniedDevice);

            return TelemetryDeviceAccess.Allow(device);
        }

        if (caller.IsInRole(PlatformRole))
            return TelemetryDeviceAccess.Allow(device);

        var tenants = await UserTenantsAsync(
            caller, action == TelemetryAction.Read ? null : WriteRoles, cancellationToken);
        return tenants.Contains(device.TenantId)
            ? TelemetryDeviceAccess.Allow(device)
            : TelemetryDeviceAccess.Deny(DeniedDevice);
    }

    public async Task<TelemetryTenantAccess> AuthorizeTenantReadAsync(
        ClaimsPrincipal caller, Guid tenantId, CancellationToken cancellationToken)
    {
        if (caller.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return TelemetryTenantAccess.Deny(DeniedTenant);

        if (caller.IsInRole(PlatformRole))
            return TelemetryTenantAccess.Allow();

        var tenants = await UserTenantsAsync(caller, null, cancellationToken);
        return tenants.Contains(tenantId)
            ? TelemetryTenantAccess.Allow()
            : TelemetryTenantAccess.Deny(DeniedTenant);
    }

    public async Task<TelemetryReadScope> ResolveReadScopeAsync(
        ClaimsPrincipal caller, Guid? requestedTenantId, CancellationToken cancellationToken)
    {
        if (caller.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return TelemetryReadScope.Forbid(DeniedTenant);

        if (caller.IsInRole(PlatformRole))
            return TelemetryReadScope.Allow(requestedTenantId);

        var tenants = await UserTenantsAsync(caller, null, cancellationToken);
        if (tenants.Count == 0)
            return TelemetryReadScope.Forbid(DeniedTenant);

        if (requestedTenantId.HasValue)
        {
            return tenants.Contains(requestedTenantId.Value)
                ? TelemetryReadScope.Allow(requestedTenantId.Value)
                : TelemetryReadScope.Forbid(DeniedTenant);
        }

        return tenants.Count == 1
            ? TelemetryReadScope.Allow(tenants[0])
            : TelemetryReadScope.NeedTenant("tenantId is required because you belong to more than one tenant.");
    }

    /// <summary>
    /// The tenants a user holds an active role in (optionally one of <paramref name="roleNames"/>).
    /// Role claims are global names and password-login tokens carry no tenant_id, so membership comes
    /// from UserRoles rows (null-tenant rows do not count), like GroupService and AuditAccessResolver.
    /// A tenant-scoped token (tenant_id claim present) only carries authority in that tenant.
    /// </summary>
    private async Task<List<Guid>> UserTenantsAsync(
        ClaimsPrincipal caller, string[]? roleNames, CancellationToken cancellationToken)
    {
        var subject = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return new List<Guid>();

        var now = DateTime.UtcNow;
        var query = _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId
                && ur.TenantId != null
                && (ur.ExpiresAt == null || ur.ExpiresAt > now));

        if (roleNames != null)
            query = query.Where(ur => roleNames.Contains(ur.Role.Name));

        var tenants = await query
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var tenantClaim = caller.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var claimedTenantId))
                return new List<Guid>();

            tenants = tenants.Where(t => t == claimedTenantId).ToList();
        }

        return tenants;
    }
}
