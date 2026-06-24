using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace IAM.API.Auth;

/// <summary>
/// Grants a SuperAdmin principal every other admin role so that endpoints
/// guarded with <c>[Authorize(Roles = "SecurityAdmin,SystemAdmin")]</c> etc.
/// are accessible to SuperAdmin without listing SuperAdmin on each attribute.
/// SuperAdmin is the highest privilege role and is intended to be omnipotent.
/// </summary>
public class SuperAdminClaimsTransformation : IClaimsTransformation
{
    // All roles referenced by [Authorize(Roles = ...)] attributes across the API.
    private static readonly string[] ImpliedRoles =
    {
        "SystemAdmin",
        "SecurityAdmin",
        "ComplianceOfficer",
        "TenantAdmin",
        "BuildingOwner",
        "BuildingManager",
        "EmergencyAccess",
    };

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity identity &&
            identity.IsAuthenticated &&
            principal.IsInRole("SuperAdmin"))
        {
            foreach (var role in ImpliedRoles)
            {
                if (!principal.IsInRole(role))
                {
                    identity.AddClaim(new Claim(identity.RoleClaimType, role));
                }
            }
        }

        return Task.FromResult(principal);
    }
}
