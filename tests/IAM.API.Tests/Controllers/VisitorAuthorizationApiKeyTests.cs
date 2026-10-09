using System.Security.Claims;
using System.Text.Json;
using Hazina.Security.ApiKeys;
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
/// Task 5151, API-key callers: a write- or admin-scope key counts as a manager of the tenant it reaches (never as a
/// host), a read-scope key is refused on every action (a QR token is a credential), and a key never inherits the roles
/// of the user who issued it. Driven straight on the controller like <see cref="DeviceManagementApiKeyTests"/>: the
/// shared test host pins the default authorization policy to JWT, which hides API-key callers from plain [Authorize]
/// endpoints; the production pipeline accepts them. Over the real service and resolver and an in-memory database.
/// </summary>
public class VisitorAuthorizationApiKeyTests
{
    private sealed class Harness
    {
        public IAMDbContext Db { get; }
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public User HostA { get; }
        public User HostB { get; }
        public Visitor VisitorA { get; }
        public Visitor VisitorB { get; }

        public Harness()
        {
            Db = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            Db.Tenants.Add(new Tenant { Id = TenantA, Name = "A", IsActive = true });
            Db.Tenants.Add(new Tenant { Id = TenantB, Name = "B", IsActive = true });
            HostA = NewUser("host-a");
            HostB = NewUser("host-b");
            Db.Users.AddRange(HostA, HostB);
            Member(HostA, TenantA);
            Member(HostB, TenantB);
            VisitorA = NewVisitor(TenantA, HostA);
            VisitorB = NewVisitor(TenantB, HostB);
            Db.Visitors.AddRange(VisitorA, VisitorB);
            Db.SaveChanges();
        }

        private static User NewUser(string name) => new()
        {
            Id = Guid.NewGuid(),
            Email = $"{name}@visitor-key.test",
            FirstName = name,
            LastName = "Person",
            PasswordHash = "x"
        };

        private static Visitor NewVisitor(Guid tenantId, User host) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            HostUserId = host.Id,
            Name = $"Visitor {Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@visitor-key-guest.test",
            VisitDate = DateTime.UtcNow.AddDays(1)
        };

        public void Member(User user, Guid tenantId, string roleName = "User")
        {
            var role = Db.Roles.Local.FirstOrDefault(r => r.Name == roleName) ?? Db.Roles.FirstOrDefault(r => r.Name == roleName);
            if (role == null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName };
                Db.Roles.Add(role);
            }

            Db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id, TenantId = tenantId });
        }

        public VisitorController ControllerFor(ClaimsPrincipal caller) =>
            new(new VisitorService(Db), new TenantAccessResolver(Db), NullLogger<VisitorController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } },
            };

        public PreRegisterVisitorRequest Register(Guid tenantId, Guid? hostUserId, string? email = null) => new(
            tenantId,
            "Key Visitor",
            email ?? $"{Guid.NewGuid():N}@visitor-key-new.test",
            "Guest BV",
            DateTime.UtcNow.AddDays(1),
            "meeting",
            hostUserId,
            null);
    }

    private static ClaimsPrincipal ApiKeyCaller(ApiKeyScope scope, Guid? tenant, Guid? issuedBy = null) =>
        ApiKeyPrincipal.Create(new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("D"),
            KeyHash = "hash",
            KeyPrefix = "iam_test_",
            Name = "caller",
            Scope = scope,
            TenantId = tenant?.ToString("D"),
            UserId = (issuedBy ?? Guid.NewGuid()).ToString("D"),
        });

    private static int? StatusOf(IActionResult result) => (result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode;

    private static void AssertStatus(int expected, IActionResult result, string what) =>
        Assert.True(expected == StatusOf(result), $"{what}: expected {expected}, got {StatusOf(result)}");

    private static List<Guid> VisitorIds(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(ok.Value);
        return json.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    // ----- read scope: refused everywhere ---------------------------------------------------------

    [Fact]
    public async Task TenantKey_WithReadScope_IsRefusedOnEveryAction_EvenInItsOwnTenant()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Read, h.TenantA));

        AssertStatus(403, await c.PreRegisterVisitor(h.Register(h.TenantA, h.HostA.Id), default), "register own");
        AssertStatus(403, await c.GetVisitors(h.TenantA), "list own");
        AssertStatus(403, await c.GetVisitor(h.VisitorA.Id, default), "get own");
        AssertStatus(403, await c.CheckIn(h.VisitorA.Id, default), "check-in own");
        AssertStatus(403, await c.CheckOut(h.VisitorA.Id, default), "check-out own");
        AssertStatus(403, await c.GetQrCode(h.VisitorA.Id, default), "qr own");
        AssertStatus(403, await c.GetVisitor(h.VisitorB.Id, default), "get foreign");
        AssertStatus(403, await c.GetQrCode(Guid.NewGuid(), default), "qr unknown id");

        Assert.Equal(VisitorStatus.PreRegistered, (await h.Db.Visitors.AsNoTracking().FirstAsync(v => v.Id == h.VisitorA.Id)).Status);
        Assert.Equal(1, await h.Db.Visitors.CountAsync(v => v.TenantId == h.TenantA));
    }

    [Fact]
    public async Task ReadKeyIssuedByTheHost_IsNotTheHost()
    {
        var h = new Harness();
        // The key carries the host's id as NameIdentifier, but it is a credential of its own: never a host.
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Read, h.TenantA, issuedBy: h.HostA.Id));

        AssertStatus(403, await c.GetVisitor(h.VisitorA.Id, default), "get");
        AssertStatus(403, await c.GetQrCode(h.VisitorA.Id, default), "qr");
        AssertStatus(403, await c.GetVisitors(h.TenantA), "list");
    }

    [Fact]
    public async Task PlatformKey_WithoutAdminScope_IsRefusedOnEveryAction()
    {
        var h = new Harness();
        foreach (var scope in new[] { ApiKeyScope.Read, ApiKeyScope.Write })
        {
            var c = h.ControllerFor(ApiKeyCaller(scope, tenant: null));

            AssertStatus(403, await c.PreRegisterVisitor(h.Register(h.TenantA, h.HostA.Id), default), $"{scope} register");
            AssertStatus(403, await c.GetVisitors(h.TenantA), $"{scope} list");
            AssertStatus(403, await c.GetVisitor(h.VisitorA.Id, default), $"{scope} get");
            AssertStatus(403, await c.GetQrCode(h.VisitorA.Id, default), $"{scope} qr");
            AssertStatus(403, await c.CheckIn(h.VisitorA.Id, default), $"{scope} check-in");
        }
    }

    // ----- write scope: manager of its own tenant, nothing else --------------------------------------

    [Fact]
    public async Task TenantKey_WithWriteScope_ManagesItsOwnTenantsVisitors()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Write, h.TenantA));
        var email = $"{Guid.NewGuid():N}@visitor-key-new.test";

        AssertStatus(200, await c.PreRegisterVisitor(h.Register(h.TenantA, h.HostA.Id, email), default), "register own");
        Assert.Equal(h.HostA.Id, (await h.Db.Visitors.AsNoTracking().SingleAsync(v => v.Email == email)).HostUserId);
        Assert.Contains(h.VisitorA.Id, VisitorIds(await c.GetVisitors(h.TenantA)));
        AssertStatus(200, await c.GetVisitor(h.VisitorA.Id, default), "get own");
        AssertStatus(200, await c.GetQrCode(h.VisitorA.Id, default), "qr own");
        AssertStatus(200, await c.CheckIn(h.VisitorA.Id, default), "check-in own");
        AssertStatus(200, await c.CheckOut(h.VisitorA.Id, default), "check-out own");
        Assert.Equal(VisitorStatus.CheckedOut, (await h.Db.Visitors.AsNoTracking().FirstAsync(v => v.Id == h.VisitorA.Id)).Status);
    }

    [Fact]
    public async Task TenantKey_WithWriteScope_NeverReachesAnotherTenant()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Write, h.TenantA));
        var email = $"{Guid.NewGuid():N}@visitor-key-new.test";

        AssertStatus(403, await c.PreRegisterVisitor(h.Register(h.TenantB, h.HostB.Id, email), default), "register foreign");
        AssertStatus(403, await c.GetVisitors(h.TenantB), "list foreign");
        AssertStatus(404, await c.GetVisitor(h.VisitorB.Id, default), "get foreign");
        AssertStatus(404, await c.GetQrCode(h.VisitorB.Id, default), "qr foreign");
        AssertStatus(404, await c.CheckIn(h.VisitorB.Id, default), "check-in foreign");
        AssertStatus(404, await c.CheckOut(h.VisitorB.Id, default), "check-out foreign");

        Assert.False(await h.Db.Visitors.AnyAsync(v => v.Email == email));
        Assert.Equal(VisitorStatus.PreRegistered, (await h.Db.Visitors.AsNoTracking().FirstAsync(v => v.Id == h.VisitorB.Id)).Status);
    }

    [Fact]
    public async Task WriteKey_CannotRegisterWithAHostWhoIsNotAMemberOfTheTenant()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Write, h.TenantA));
        var email = $"{Guid.NewGuid():N}@visitor-key-new.test";

        AssertStatus(400, await c.PreRegisterVisitor(h.Register(h.TenantA, h.HostB.Id, email), default), "host of another tenant");
        // No host named: it falls back to the key's issuing user, who is not a member of the tenant either.
        AssertStatus(400, await c.PreRegisterVisitor(h.Register(h.TenantA, null, email), default), "issuer is not a member");
        Assert.False(await h.Db.Visitors.AnyAsync(v => v.Email == email));
    }

    [Fact]
    public async Task WriteKey_IssuedByAManagerOfAnotherTenant_DoesNotInheritThatUsersRoles()
    {
        var h = new Harness();
        var manager = new User { Id = Guid.NewGuid(), Email = "m@visitor-key.test", PasswordHash = "x" };
        h.Db.Users.Add(manager);
        h.Member(manager, h.TenantB, "BuildingManager");
        await h.Db.SaveChangesAsync();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Write, h.TenantA, issuedBy: manager.Id));
        var email = $"{Guid.NewGuid():N}@visitor-key-new.test";

        AssertStatus(403, await c.PreRegisterVisitor(h.Register(h.TenantB, h.HostB.Id, email), default), "register in the issuer's tenant");
        AssertStatus(403, await c.GetVisitors(h.TenantB), "list in the issuer's tenant");
        AssertStatus(404, await c.GetVisitor(h.VisitorB.Id, default), "get in the issuer's tenant");
        AssertStatus(404, await c.CheckIn(h.VisitorB.Id, default), "check-in in the issuer's tenant");
        Assert.False(await h.Db.Visitors.AnyAsync(v => v.Email == email));
    }

    // ----- admin scope ------------------------------------------------------------------------------

    [Fact]
    public async Task PlatformKey_WithAdminScope_ManagesEveryTenant()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Admin, tenant: null));
        var email = $"{Guid.NewGuid():N}@visitor-key-new.test";

        AssertStatus(200, await c.PreRegisterVisitor(h.Register(h.TenantB, h.HostB.Id, email), default), "register");
        Assert.Contains(h.VisitorA.Id, VisitorIds(await c.GetVisitors(h.TenantA)));
        Assert.Contains(h.VisitorB.Id, VisitorIds(await c.GetVisitors(h.TenantB)));
        AssertStatus(200, await c.GetVisitor(h.VisitorA.Id, default), "get a");
        AssertStatus(200, await c.GetQrCode(h.VisitorB.Id, default), "qr b");
        AssertStatus(200, await c.CheckIn(h.VisitorB.Id, default), "check-in b");
        AssertStatus(404, await c.GetVisitor(Guid.NewGuid(), default), "unknown id");
        // The admin key is not a user: it still has to name a host that belongs to the tenant.
        AssertStatus(400, await c.PreRegisterVisitor(h.Register(h.TenantB, h.HostA.Id), default), "host of another tenant");
    }

    [Fact]
    public async Task TenantKey_WithAdminScope_StaysInsideItsTenant()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Admin, h.TenantA));

        AssertStatus(200, await c.GetVisitor(h.VisitorA.Id, default), "get own");
        AssertStatus(404, await c.GetVisitor(h.VisitorB.Id, default), "get foreign");
        AssertStatus(403, await c.GetVisitors(h.TenantB), "list foreign");
    }
}
