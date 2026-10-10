using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Who may manage one tenant's members and invitations, answered from UserRoles (task 4700): a SuperAdmin
/// (a token role claim, platform-wide), or a user holding an active BuildingOwner/BuildingManager row scoped to
/// exactly that tenant. A row without a tenant, an expired row, or a role claim alone confers nothing.
/// </summary>
public static class TenantManagementAuthority
{
    public const string OwnerRoleName = "BuildingOwner";
    public const string ManagerRoleName = "BuildingManager";

    /// <returns>The caller's grantor level in the tenant, or null when they may not manage it.</returns>
    public static async Task<TenantGrantor?> ResolveAsync(
        IAMDbContext context, Guid userId, bool isSuperAdmin, Guid tenantId, CancellationToken ct)
    {
        if (isSuperAdmin)
            return TenantGrantor.SuperAdmin;

        var now = DateTime.UtcNow;
        var roleNames = await context.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId
                && ur.TenantId == tenantId
                && (ur.ExpiresAt == null || ur.ExpiresAt > now)
                && (ur.Role.Name == OwnerRoleName || ur.Role.Name == ManagerRoleName))
            .Select(ur => ur.Role.Name)
            .ToListAsync(ct);

        if (roleNames.Count == 0)
            return null;

        return roleNames.Contains(OwnerRoleName) ? TenantGrantor.Owner : TenantGrantor.Manager;
    }

    /// <summary>True when the user manages at least one tenant (or is a SuperAdmin) - the coarse gate for id-only routes.</summary>
    public static async Task<bool> ManagesAnyTenantAsync(IAMDbContext context, Guid userId, bool isSuperAdmin, CancellationToken ct)
    {
        if (isSuperAdmin)
            return true;

        var now = DateTime.UtcNow;
        return await context.UserRoles.AnyAsync(ur => ur.UserId == userId
            && ur.TenantId != null
            && (ur.ExpiresAt == null || ur.ExpiresAt > now)
            && (ur.Role.Name == OwnerRoleName || ur.Role.Name == ManagerRoleName), ct);
    }

    /// <summary>The tenants where the user holds an active BuildingOwner/BuildingManager row (empty for none; SuperAdmin is not expanded).</summary>
    public static async Task<List<Guid>> GetManagedTenantIdsAsync(IAMDbContext context, Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await context.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId
                && ur.TenantId != null
                && (ur.ExpiresAt == null || ur.ExpiresAt > now)
                && (ur.Role.Name == OwnerRoleName || ur.Role.Name == ManagerRoleName))
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>The user ids holding an active BuildingOwner row in the tenant.</summary>
    public static Task<List<Guid>> GetActiveOwnerIdsAsync(IAMDbContext context, Guid tenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return context.UserRoles.AsNoTracking()
            .Where(ur => ur.TenantId == tenantId
                && ur.Role.Name == OwnerRoleName
                && (ur.ExpiresAt == null || ur.ExpiresAt > now))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(ct);
    }
}
