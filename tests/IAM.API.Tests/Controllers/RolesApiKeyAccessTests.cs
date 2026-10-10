using System.Security.Claims;
using System.Text.Json;
using Hazina.Security.ApiKeys;
using IAM.API.Authorization;
using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5163, API-key callers of the role endpoints: a read-scope key is not an administrator and gets 403, a
/// write-scope tenant key administers its own tenant only, a platform key needs admin scope to see every role. Driven
/// straight on the controller (like <see cref="DeviceManagementApiKeyTests"/>: the shared test host pins the default
/// authorization policy to JWT, which hides API-key callers from plain [Authorize] endpoints; production accepts them).
/// </summary>
public class RolesApiKeyAccessTests
{
    private sealed class Harness
    {
        public IAMDbContext Db { get; }
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();

        public Harness()
        {
            Db = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            Db.Tenants.Add(new Tenant { Id = TenantA, Name = "A", IsActive = true });
            Db.Tenants.Add(new Tenant { Id = TenantB, Name = "B", IsActive = true });
            Db.Roles.Add(new Role { Id = Guid.NewGuid(), Name = "global-role", TenantId = null });
            Db.Roles.Add(new Role { Id = Guid.NewGuid(), Name = "role-a", TenantId = TenantA });
            Db.Roles.Add(new Role { Id = Guid.NewGuid(), Name = "role-b", TenantId = TenantB });
            Db.SaveChanges();
        }

        public RolesController ControllerFor(ClaimsPrincipal caller) =>
            new(Db, new TenantAccessResolver(Db))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } },
            };
    }

    private static ClaimsPrincipal ApiKeyCaller(ApiKeyScope scope, Guid? tenant) =>
        ApiKeyPrincipal.Create(new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("D"),
            KeyHash = "hash",
            KeyPrefix = "iam_test_",
            Name = "caller",
            Scope = scope,
            TenantId = tenant?.ToString("D"),
            UserId = Guid.NewGuid().ToString("D"),
        });

    private static int? StatusOf(IActionResult result) => (result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode;

    private static List<string> RoleNames(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(ok.Value);
        return json.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();
    }

    [Theory]
    [InlineData(ApiKeyScope.Read)]
    [InlineData(ApiKeyScope.Write)]
    public async Task PlatformKey_BelowAdminScope_IsNotAnAdministrator(ApiKeyScope scope)
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(scope, null));

        Assert.Equal(403, StatusOf(await c.ListRoles(CancellationToken.None)));
        Assert.Equal(403, StatusOf(await c.GetRole(Guid.NewGuid(), CancellationToken.None)));
    }

    [Fact]
    public async Task TenantKey_WithReadScope_IsNotAnAdministrator()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Read, h.TenantA));

        Assert.Equal(403, StatusOf(await c.ListRoles(CancellationToken.None)));
    }

    [Fact]
    public async Task TenantKey_WithWriteScope_SeesGlobalAndOwnTenantRolesOnly()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Write, h.TenantA));

        var names = RoleNames(await c.ListRoles(CancellationToken.None));

        Assert.Contains("global-role", names);
        Assert.Contains("role-a", names);
        Assert.DoesNotContain("role-b", names);
    }

    [Fact]
    public async Task PlatformKey_WithAdminScope_SeesEveryRole()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Admin, null));

        var names = RoleNames(await c.ListRoles(CancellationToken.None));

        Assert.Contains("global-role", names);
        Assert.Contains("role-a", names);
        Assert.Contains("role-b", names);
    }
}
