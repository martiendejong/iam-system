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

    private static UserRole Named(string role, Guid? tenantId, DateTime? grantedAt = null) => new()
    {
        Role = new Role { Name = role },
        TenantId = tenantId,
        GrantedAt = grantedAt ?? DateTime.UtcNow
    };

    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void SelectTenant_NoRequest_PicksEarliestAssignment()
    {
        var early = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = early.AddDays(1);
        var result = AuthorizationController.SelectTenant(new[]
        {
            Named("app:customer", TenantA, late),
            Named("app:admin", TenantB, early)
        }, null);
        Assert.False(result.Refused);
        Assert.Equal(TenantB, result.TenantId);
    }

    [Fact]
    public void SelectTenant_EqualTimestamps_FallsBackToLowestTenantId_RegardlessOfOrder()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var one = AuthorizationController.SelectTenant(new[] { Named("a", TenantB, at), Named("b", TenantA, at) }, null);
        var two = AuthorizationController.SelectTenant(new[] { Named("b", TenantA, at), Named("a", TenantB, at) }, null);
        Assert.Equal(TenantA, one.TenantId);
        Assert.Equal(TenantA, two.TenantId);
    }

    [Fact]
    public void SelectTenant_RequestedTenantUserBelongsTo_IsHonoured()
    {
        var result = AuthorizationController.SelectTenant(new[] { Named("a", TenantA), Named("b", TenantB) }, TenantB);
        Assert.False(result.Refused);
        Assert.Equal(TenantB, result.TenantId);
    }

    [Fact]
    public void SelectTenant_RequestedTenantWithoutRole_IsRefused()
    {
        var result = AuthorizationController.SelectTenant(new[] { Named("a", TenantA), Named("g", null) }, TenantB);
        Assert.True(result.Refused);
        Assert.Null(result.TenantId);
    }

    [Fact]
    public void SelectTenant_OnlyGlobalRoles_NoTenant()
    {
        var result = AuthorizationController.SelectTenant(new[] { Named("g", null) }, null);
        Assert.False(result.Refused);
        Assert.Null(result.TenantId);
    }

    [Fact]
    public void RoleNamesForTenant_AdminInAAndCustomerInB_EachTokenCarriesOnlyItsOwnRole()
    {
        var assignments = new[]
        {
            Named("app:admin", TenantA),
            Named("app:customer", TenantB),
            Named("app:viewer", null)
        };
        var forA = AuthorizationController.RoleNamesForTenant(assignments, TenantA, scoped: true);
        var forB = AuthorizationController.RoleNamesForTenant(assignments, TenantB, scoped: true);
        Assert.Equal(new[] { "app:admin", "app:viewer" }, forA.OrderBy(x => x));
        Assert.Equal(new[] { "app:customer", "app:viewer" }, forB.OrderBy(x => x));
    }

    [Fact]
    public void RoleNamesForTenant_NoTenant_GivesGlobalRolesOnly()
    {
        var assignments = new[] { Named("app:admin", TenantA), Named("app:viewer", null) };
        Assert.Equal(new[] { "app:viewer" }, AuthorizationController.RoleNamesForTenant(assignments, null, scoped: true));
    }

    [Fact]
    public void RoleNamesForTenant_AppWithoutCatalog_KeepsEveryRoleAsBefore()
    {
        var assignments = new[] { Named("x", TenantA), Named("y", TenantB), Named("z", null), Named("z", null) };
        var result = AuthorizationController.RoleNamesForTenant(assignments, null, scoped: false);
        Assert.Equal(new[] { "x", "y", "z" }, result.OrderBy(x => x));
    }

    [Fact]
    public void RefreshKeepsTenant_SelectingStoredTenantAgainYieldsSameTenantAndRoles()
    {
        // The refresh grant calls SelectTenant with the tenant_id stored in the refresh token as the request.
        var assignments = new[] { Named("app:admin", TenantA, DateTime.UtcNow.AddDays(-2)), Named("app:customer", TenantB, DateTime.UtcNow.AddDays(-1)) };
        var login = AuthorizationController.SelectTenant(assignments, TenantB);
        var refresh = AuthorizationController.SelectTenant(assignments, login.TenantId);
        Assert.Equal(login.TenantId, refresh.TenantId);
        Assert.Equal(new[] { "app:customer" }, AuthorizationController.RoleNamesForTenant(assignments, refresh.TenantId, true));
    }

    [Fact]
    public void ResolveAppRoleTenantId_MultipleScopedAssignments_ReturnsTheEarliestOne()
    {
        var firstTenantId = Guid.NewGuid();
        var secondTenantId = Guid.NewGuid();
        var first = Assignment(firstTenantId);
        first.GrantedAt = DateTime.UtcNow.AddDays(-2);
        var second = Assignment(secondTenantId);
        second.GrantedAt = DateTime.UtcNow.AddDays(-1);
        Assert.Equal(firstTenantId, AuthorizationController.ResolveAppRoleTenantId(new[] { second, first }));
    }
}
