using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Who may manage a tenant's directory sync (task 4698): a SuperAdmin, or a user holding an active (not expired)
/// BuildingOwner UserRole scoped to exactly that tenant. A role claim alone, an owner of another tenant, a manager,
/// an unscoped row or an expired row confer nothing.
/// </summary>
public static class DirectorySyncAuthority
{
    public const string OwnerRoleName = "BuildingOwner";

    public static Task<bool> IsOwnerOfTenantAsync(IAMDbContext context, Guid userId, Guid tenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return context.UserRoles.AnyAsync(ur =>
            ur.UserId == userId
            && ur.TenantId == tenantId
            && ur.Role.Name == OwnerRoleName
            && (ur.ExpiresAt == null || ur.ExpiresAt > now), ct);
    }

    /// <summary>The coarse gate for id-only routes: the user owns at least one tenant.</summary>
    public static Task<bool> OwnsAnyTenantAsync(IAMDbContext context, Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return context.UserRoles.AnyAsync(ur =>
            ur.UserId == userId
            && ur.TenantId != null
            && ur.Role.Name == OwnerRoleName
            && (ur.ExpiresAt == null || ur.ExpiresAt > now), ct);
    }
}
