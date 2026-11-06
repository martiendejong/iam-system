using System.Security.Claims;
using IAM.API.Authorization;
using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5850: the temporary-access and maintenance API is scoped to the caller's own tenants. Two tenants (A, B),
/// an owner of each, a manager of A and a plain member of A; driven straight on the controller over the real engine
/// and an in-memory database so the tests can also assert nothing was stored or changed.
/// </summary>
public class TemporalPolicyTenantScopeTests
{
    private sealed class Harness
    {
        public IAMDbContext Db { get; }
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public Guid OwnerA { get; } = Guid.NewGuid();
        public Guid OwnerB { get; } = Guid.NewGuid();
        public Guid ManagerA { get; } = Guid.NewGuid();
        public Guid MemberA { get; } = Guid.NewGuid();
        public Guid Target { get; } = Guid.NewGuid();
        public Role OwnerRole { get; } = new() { Id = Guid.NewGuid(), Name = "BuildingOwner" };
        public Role ManagerRole { get; } = new() { Id = Guid.NewGuid(), Name = "BuildingManager" };
        public Role PlainRole { get; } = new() { Id = Guid.NewGuid(), Name = "Visitor" };
        public Role PlatformRole { get; } = new() { Id = Guid.NewGuid(), Name = "SystemAdmin" };
        public Role OtherTenantRole { get; }
        public Role FederatedRole { get; } = new() { Id = Guid.NewGuid(), Name = "Visitor2", Category = "app:client-1" };
        public Role PrivilegedRole { get; } = new() { Id = Guid.NewGuid(), Name = "Security Auditor Admin" };
        public Policy PolicyA { get; }
        public Policy PolicyB { get; }

        public Harness()
        {
            Db = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            Db.Tenants.Add(new Tenant { Id = TenantA, Name = "A", IsActive = true });
            Db.Tenants.Add(new Tenant { Id = TenantB, Name = "B", IsActive = true });
            OtherTenantRole = new Role { Id = Guid.NewGuid(), Name = "Custom", TenantId = TenantB };
            Db.Roles.AddRange(OwnerRole, ManagerRole, PlainRole, PlatformRole, OtherTenantRole, FederatedRole, PrivilegedRole);
            foreach (var id in new[] { OwnerA, OwnerB, ManagerA, MemberA, Target })
                Db.Users.Add(new User { Id = id, Email = $"{id:N}@test.com", PasswordHash = "x" });
            Db.UserRoles.AddRange(
                Row(OwnerA, OwnerRole, TenantA),
                Row(OwnerB, OwnerRole, TenantB),
                Row(ManagerA, ManagerRole, TenantA),
                Row(MemberA, PlainRole, TenantA));
            PolicyA = new Policy { Id = Guid.NewGuid(), Name = "pa", TenantId = TenantA, IsActive = true };
            PolicyB = new Policy { Id = Guid.NewGuid(), Name = "pb", TenantId = TenantB, IsActive = true };
            Db.Policies.AddRange(PolicyA, PolicyB);
            Db.SaveChanges();
        }

        private static UserRole Row(Guid userId, Role role, Guid tenantId) =>
            new() { Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenantId };

        public TemporalPolicyController ControllerFor(ClaimsPrincipal caller) =>
            new(new TemporalPolicyEngine(Db, NullLogger<TemporalPolicyEngine>.Instance),
                new TenantAccessResolver(Db), Db, NullLogger<TemporalPolicyController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } },
            };

        public GrantTemporaryAccessRequest Grant(Guid tenantId, Role role, Guid? userId = null) => new()
        {
            UserId = userId ?? Target,
            RoleId = role.Id,
            TenantId = tenantId,
            StartTime = DateTime.UtcNow.AddMinutes(-1),
            EndTime = DateTime.UtcNow.AddHours(2),
            Justification = "incident",
        };

        public TemporaryAccessGrant SeedGrant(Guid tenantId)
        {
            var g = new TemporaryAccessGrant
            {
                UserId = Target, RoleId = PlainRole.Id, TenantId = tenantId,
                StartTime = DateTime.UtcNow.AddMinutes(-1), EndTime = DateTime.UtcNow.AddHours(1),
                Status = TemporaryAccessStatus.Active, Justification = "seed",
            };
            Db.TemporaryAccessGrants.Add(g);
            Db.SaveChanges();
            return g;
        }
    }

    private static ClaimsPrincipal User(Guid id, params string[] roles) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) }.Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))),
            "test"));

    private static int? StatusOf(IActionResult result) =>
        (result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode;

    private static async Task<int?> StatusAsync<T>(Task<ActionResult<T>> call) => StatusOf((await call).Result!);

    // ---- grant ----

    [Fact]
    public async Task Grant_OwnerOfTenant_Succeeds()
    {
        var h = new Harness();
        var result = await h.ControllerFor(User(h.OwnerA, "TenantAdmin")).GrantTemporaryAccess(h.Grant(h.TenantA, h.PlainRole));
        Assert.Equal(200, StatusOf(result.Result!));
        Assert.Equal(1, h.Db.TemporaryAccessGrants.Count());
    }

    [Fact]
    public async Task Grant_AdminOfOtherTenant_Is403_AndStoresNothing()
    {
        var h = new Harness();
        var result = await h.ControllerFor(User(h.OwnerB, "TenantAdmin")).GrantTemporaryAccess(h.Grant(h.TenantA, h.PlainRole));
        Assert.Equal(403, StatusOf(result.Result!));
        Assert.Empty(h.Db.TemporaryAccessGrants);
    }

    [Fact]
    public async Task Grant_PlainMember_Is403()
    {
        var h = new Harness();
        var result = await h.ControllerFor(User(h.MemberA)).GrantTemporaryAccess(h.Grant(h.TenantA, h.PlainRole));
        Assert.Equal(403, StatusOf(result.Result!));
        Assert.Empty(h.Db.TemporaryAccessGrants);
    }

    [Fact]
    public async Task Grant_SystemAdminClaimWithoutRowInTenant_Is403()
    {
        var h = new Harness();
        var result = await h.ControllerFor(User(Guid.NewGuid(), "SystemAdmin", "TenantAdmin")).GrantTemporaryAccess(h.Grant(h.TenantA, h.PlainRole));
        Assert.Equal(403, StatusOf(result.Result!));
        Assert.Empty(h.Db.TemporaryAccessGrants);
    }

    [Fact]
    public async Task Grant_DeviceToken_Is403()
    {
        var h = new Harness();
        var caller = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, h.OwnerA.ToString()),
            new Claim(ServiceAccountAuthorization.TokenTypeClaim, "device"),
        }, "test"));
        var result = await h.ControllerFor(caller).GrantTemporaryAccess(h.Grant(h.TenantA, h.PlainRole));
        Assert.Equal(403, StatusOf(result.Result!));
        Assert.Empty(h.Db.TemporaryAccessGrants);
    }

    [Fact]
    public async Task Grant_ManagerCannotGrantOwnerLevelRole()
    {
        var h = new Harness();
        var result = await h.ControllerFor(User(h.ManagerA)).GrantTemporaryAccess(h.Grant(h.TenantA, h.OwnerRole));
        Assert.Equal(403, StatusOf(result.Result!));
        Assert.Empty(h.Db.TemporaryAccessGrants);
    }

    [Fact]
    public async Task Grant_OwnerCanGrantOwnerLevelRole()
    {
        var h = new Harness();
        var result = await h.ControllerFor(User(h.OwnerA)).GrantTemporaryAccess(h.Grant(h.TenantA, h.OwnerRole));
        Assert.Equal(200, StatusOf(result.Result!));
    }

    [Fact]
    public async Task Grant_RolesTheCallerMayNotAssign_Are403()
    {
        var h = new Harness();
        foreach (var role in new[] { h.PlatformRole, h.OtherTenantRole, h.FederatedRole, h.PrivilegedRole })
        {
            var result = await h.ControllerFor(User(h.OwnerA)).GrantTemporaryAccess(h.Grant(h.TenantA, role));
            Assert.True(StatusOf(result.Result!) == 403, $"role {role.Name} should be refused");
        }
        Assert.Empty(h.Db.TemporaryAccessGrants);
    }

    [Fact]
    public async Task Grant_ToSelf_IsRefused_ForOwner()
    {
        var h = new Harness();
        var result = await h.ControllerFor(User(h.OwnerA)).GrantTemporaryAccess(h.Grant(h.TenantA, h.PlainRole, h.OwnerA));
        Assert.Equal(403, StatusOf(result.Result!));
        Assert.Empty(h.Db.TemporaryAccessGrants);
    }

    [Fact]
    public async Task Grant_SuperAdmin_CanGrantAnyRoleAnywhere_AndToSelf()
    {
        var h = new Harness();
        var superAdmin = Guid.NewGuid();
        var c = h.ControllerFor(User(superAdmin, "SuperAdmin"));
        Assert.Equal(200, StatusOf((await c.GrantTemporaryAccess(h.Grant(h.TenantB, h.PlatformRole))).Result!));
        h.Db.Users.Add(new User { Id = superAdmin, Email = "sa@test.com", PasswordHash = "x" });
        h.Db.SaveChanges();
        Assert.Equal(200, StatusOf((await c.GrantTemporaryAccess(h.Grant(h.TenantA, h.PlainRole, superAdmin))).Result!));
    }

    // ---- revoke ----

    [Fact]
    public async Task Revoke_OwnerOfGrantTenant_Succeeds()
    {
        var h = new Harness();
        var grant = h.SeedGrant(h.TenantA);
        var result = await h.ControllerFor(User(h.OwnerA)).RevokeTemporaryAccess(grant.Id, new RevokeAccessRequest { Reason = "done" });
        Assert.Equal(200, StatusOf(result.Result!));
        Assert.Equal(TemporaryAccessStatus.Revoked, h.Db.TemporaryAccessGrants.Single().Status);
    }

    [Fact]
    public async Task Revoke_GrantOfOtherTenant_Is404_AndUnchanged()
    {
        var h = new Harness();
        var grant = h.SeedGrant(h.TenantA);
        var other = await h.ControllerFor(User(h.OwnerB)).RevokeTemporaryAccess(grant.Id, new RevokeAccessRequest { Reason = "x" });
        var unknown = await h.ControllerFor(User(h.OwnerB)).RevokeTemporaryAccess(Guid.NewGuid(), new RevokeAccessRequest { Reason = "x" });
        Assert.Equal(404, StatusOf(other.Result!));
        Assert.Equal(404, StatusOf(unknown.Result!));
        var stored = h.Db.TemporaryAccessGrants.AsNoTracking().Single();
        Assert.Equal(TemporaryAccessStatus.Active, stored.Status);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task Revoke_PlainMember_Is403_AndUnchanged()
    {
        var h = new Harness();
        var grant = h.SeedGrant(h.TenantA);
        var result = await h.ControllerFor(User(h.MemberA)).RevokeTemporaryAccess(grant.Id, new RevokeAccessRequest { Reason = "x" });
        Assert.Equal(403, StatusOf(result.Result!));
        Assert.Equal(TemporaryAccessStatus.Active, h.Db.TemporaryAccessGrants.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task Revoke_SuperAdmin_CanRevokeAnyGrant()
    {
        var h = new Harness();
        var grant = h.SeedGrant(h.TenantB);
        var result = await h.ControllerFor(User(Guid.NewGuid(), "SuperAdmin")).RevokeTemporaryAccess(grant.Id, new RevokeAccessRequest { Reason = "x" });
        Assert.Equal(200, StatusOf(result.Result!));
    }

    // ---- access/active ----

    [Fact]
    public async Task ActiveAccess_OwnGrants_Allowed_OthersDenied_ManagerAllowed()
    {
        var h = new Harness();
        h.SeedGrant(h.TenantA);
        Assert.Equal(200, StatusOf((await h.ControllerFor(User(h.MemberA)).GetActiveTemporaryAccess(h.MemberA, h.TenantA)).Result!));
        Assert.Equal(403, StatusOf((await h.ControllerFor(User(h.MemberA)).GetActiveTemporaryAccess(h.Target, h.TenantA)).Result!));
        Assert.Equal(403, StatusOf((await h.ControllerFor(User(h.OwnerB)).GetActiveTemporaryAccess(h.Target, h.TenantA)).Result!));
        Assert.Equal(200, StatusOf((await h.ControllerFor(User(h.ManagerA)).GetActiveTemporaryAccess(h.Target, h.TenantA)).Result!));
        Assert.Equal(200, StatusOf((await h.ControllerFor(User(Guid.NewGuid(), "SuperAdmin")).GetActiveTemporaryAccess(h.Target, h.TenantA)).Result!));
    }

    // ---- maintenance + policies ----

    [Fact]
    public async Task Maintenance_MembersOnly()
    {
        var h = new Harness();
        Assert.Equal(200, StatusOf((await h.ControllerFor(User(h.MemberA)).GetActiveMaintenanceWindows(h.TenantA)).Result!));
        Assert.Equal(403, StatusOf((await h.ControllerFor(User(h.OwnerB)).GetActiveMaintenanceWindows(h.TenantA)).Result!));
        Assert.Equal(200, StatusOf(await h.ControllerFor(User(h.MemberA)).CheckMaintenanceStatus(h.TenantA)));
        Assert.Equal(403, StatusOf(await h.ControllerFor(User(h.OwnerB)).CheckMaintenanceStatus(h.TenantA)));
        Assert.Equal(200, StatusOf((await h.ControllerFor(User(Guid.NewGuid(), "SuperAdmin")).GetActiveMaintenanceWindows(h.TenantA)).Result!));
    }

    [Fact]
    public async Task PolicyActive_UnreachableTenantPolicy_Is404_LikeUnknown()
    {
        var h = new Harness();
        Assert.Equal(200, StatusOf((await h.ControllerFor(User(h.MemberA)).IsPolicyActiveAt(h.PolicyA.Id, null)).Result!));
        Assert.Equal(404, StatusOf((await h.ControllerFor(User(h.MemberA)).IsPolicyActiveAt(h.PolicyB.Id, null)).Result!));
        Assert.Equal(404, StatusOf((await h.ControllerFor(User(h.MemberA)).IsPolicyActiveAt(Guid.NewGuid(), null)).Result!));
        Assert.Equal(200, StatusOf((await h.ControllerFor(User(Guid.NewGuid(), "SuperAdmin")).IsPolicyActiveAt(h.PolicyB.Id, null)).Result!));
    }
}
