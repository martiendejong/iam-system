using IAM.API.Controllers;
using IAM.Core.Entities;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Unit tests for AuthorizationController.ResolveAppRoleTenantId - the logic that decides
/// which tenant_id claim (if any) a human OIDC login token gets for an app-scoped role like
/// "taskmanager:customer" (task 1741). Pure unit tests against the static helper rather than
/// driving the full /connect/authorize OpenIddict pipeline, which none of this controller's
/// other behavior (e.g. the pre-existing app-role access gate) has integration coverage for
/// either; the end-to-end token shape is verified separately via a live login round-trip.
/// </summary>
public class AuthorizationControllerTenantClaimTests
{
    private static UserRole Assignment(Guid? tenantId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        RoleId = Guid.NewGuid(),
        TenantId = tenantId
    };

    [Fact]
    public void ResolveAppRoleTenantId_SingleTenantScopedAssignment_ReturnsThatTenant()
    {
        var tenantId = Guid.NewGuid();
        var result = AuthorizationController.ResolveAppRoleTenantId(new[] { Assignment(tenantId) });
        Assert.Equal(tenantId, result);
    }

    [Fact]
    public void ResolveAppRoleTenantId_GlobalUnscopedAssignment_ReturnsNull()
    {
        var result = AuthorizationController.ResolveAppRoleTenantId(new[] { Assignment(null) });
        Assert.Null(result);
    }

    [Fact]
    public void ResolveAppRoleTenantId_NoAssignments_ReturnsNull()
    {
        var result = AuthorizationController.ResolveAppRoleTenantId(Array.Empty<UserRole>());
        Assert.Null(result);
    }

    [Fact]
    public void ResolveAppRoleTenantId_MixOfGlobalAndScoped_PrefersTheScopedOne()
    {
        var tenantId = Guid.NewGuid();
        var result = AuthorizationController.ResolveAppRoleTenantId(new[]
        {
            Assignment(null),
            Assignment(tenantId)
        });
        Assert.Equal(tenantId, result);
    }

    [Fact]
    public void ResolveAppRoleTenantId_MultipleScopedAssignments_ReturnsTheFirstOne()
    {
        var firstTenantId = Guid.NewGuid();
        var secondTenantId = Guid.NewGuid();
        var result = AuthorizationController.ResolveAppRoleTenantId(new[]
        {
            Assignment(firstTenantId),
            Assignment(secondTenantId)
        });
        Assert.Equal(firstTenantId, result);
    }
}
