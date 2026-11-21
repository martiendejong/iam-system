using System.Security.Claims;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Authorization;

/// <summary>
/// Task 4718: who may call the device-auth claims / authorize / heartbeat endpoints for a given device.
/// - a device access token (token_type=device) acts for its own device_id only;
/// - an edge gateway (device token whose device_type is "gateway", or a service account holding
///   <see cref="DeviceEndpointAuthorizer.GatewayPermission"/>) may act for devices of its own tenant;
/// - everything else (user tokens, other service accounts) is refused.
/// </summary>
public interface IDeviceEndpointAuthorizer
{
    Task<bool> MayActForAsync(ClaimsPrincipal caller, string? deviceId, CancellationToken cancellationToken);
}

public class DeviceEndpointAuthorizer : IDeviceEndpointAuthorizer
{
    public const string GatewayPermission = "device-auth:gateway";
    public const string GatewayDeviceType = "gateway";

    private readonly IAMDbContext _context;

    public DeviceEndpointAuthorizer(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<bool> MayActForAsync(ClaimsPrincipal caller, string? deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || caller.Identity?.IsAuthenticated != true)
            return false;

        var tokenType = caller.FindFirst(ServiceAccountAuthorization.TokenTypeClaim)?.Value;

        if (tokenType == "device")
        {
            var own = caller.FindFirst("device_id")?.Value;
            if (own != null && string.Equals(own, deviceId, StringComparison.Ordinal))
                return true;

            if (!string.Equals(caller.FindFirst("device_type")?.Value, GatewayDeviceType, StringComparison.Ordinal))
                return false;
            return await SameTenantAsync(caller, deviceId, cancellationToken);
        }

        if (ServiceAccountAuthorization.HasPermission(caller, GatewayPermission))
        {
            var tenantClaim = caller.FindFirst("tenant_id")?.Value;
            if (string.IsNullOrWhiteSpace(tenantClaim))
                return true; // unscoped gateway credential
            return await SameTenantAsync(caller, deviceId, cancellationToken);
        }

        return false;
    }

    private async Task<bool> SameTenantAsync(ClaimsPrincipal caller, string deviceId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(caller.FindFirst("tenant_id")?.Value, out var callerTenant))
            return false;

        var targetTenant = await _context.Devices
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId)
            .Select(d => (Guid?)d.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        return targetTenant == callerTenant;
    }
}
