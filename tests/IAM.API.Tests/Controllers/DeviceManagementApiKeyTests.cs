using System.Security.Claims;
using System.Text.Json;
using Hazina.Security.ApiKeys;
using IAM.API.Authorization;
using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4726, API-key callers (Terraform provider, scripts): a read-scope key reads and a write-scope key changes,
/// always inside the tenant the key reaches; a platform key only reaches tenants with admin scope. Driven straight on
/// the controller (like <see cref="ApiKeyIssueGuardTests"/>: the shared test host pins the default authorization
/// policy to JWT, which hides API-key callers from plain [Authorize] endpoints; the production pipeline accepts them)
/// over the real service and an in-memory database.
/// </summary>
public class DeviceManagementApiKeyTests
{
    private sealed class Harness
    {
        public IAMDbContext Db { get; }
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public Device DeviceA { get; }
        public Device DeviceB { get; }

        public Harness()
        {
            Db = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            Db.Tenants.Add(new Tenant { Id = TenantA, Name = "A", IsActive = true });
            Db.Tenants.Add(new Tenant { Id = TenantB, Name = "B", IsActive = true });
            DeviceA = NewDevice(TenantA);
            DeviceB = NewDevice(TenantB);
            Db.Devices.AddRange(DeviceA, DeviceB);
            Db.SaveChanges();
        }

        private static Device NewDevice(Guid tenantId) => new()
        {
            Id = Guid.NewGuid(),
            DeviceId = $"dev-{Guid.NewGuid():N}",
            Name = "seeded",
            DeviceType = "sensor",
            AuthenticationMethod = "hmac",
            TenantId = tenantId,
            ResourcePath = "acme:hq:floor-1:sensor:1",
            IsActive = true
        };

        public DevicesController ControllerFor(ClaimsPrincipal caller) =>
            new(new DeviceService(Db), new TenantAccessResolver(Db))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } },
            };

        public RegisterDeviceRequest Register(Guid tenantId, string? deviceId = null) => new()
        {
            DeviceId = deviceId ?? $"new-{Guid.NewGuid():N}",
            Name = "New device",
            DeviceType = "sensor",
            AuthenticationMethod = "hmac",
            TenantId = tenantId,
            ResourcePath = "acme:hq:floor-2:sensor:9",
            Permissions = new List<string> { "acme:hq:floor-2:sensor:9:telemetry:write" },
        };
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

    private static List<string> DeviceIds(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.SerializeToElement(ok.Value);
        return json.EnumerateArray().Select(e => e.GetProperty("deviceId").GetString()!).ToList();
    }

    // ----- read scope -------------------------------------------------------------------------

    [Fact]
    public async Task TenantKey_WithReadScope_ReadsOwnTenant_AndNothingElse()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Read, h.TenantA));

        AssertStatus(200, await c.GetDevice(h.DeviceA.Id), "get own");
        AssertStatus(200, await c.GetDeviceByDeviceId(h.DeviceA.DeviceId), "by-device-id own");
        AssertStatus(200, await c.GetDevicesByTenant(h.TenantA), "by-tenant own");
        AssertStatus(200, await c.GetDevicesByType("sensor", h.TenantA), "by-type own");
        AssertStatus(200, await c.GetStatistics(h.TenantA), "statistics own");

        AssertStatus(404, await c.GetDevice(h.DeviceB.Id), "get foreign");
        AssertStatus(404, await c.GetDeviceByDeviceId(h.DeviceB.DeviceId), "by-device-id foreign");
        AssertStatus(403, await c.GetDevicesByTenant(h.TenantB), "by-tenant foreign");
        AssertStatus(403, await c.GetDevicesByType("sensor", h.TenantB), "by-type foreign");
        AssertStatus(403, await c.GetStatistics(h.TenantB), "statistics foreign");

        Assert.Equal(new[] { h.DeviceA.DeviceId }, DeviceIds(await c.GetDevices()));
        Assert.Equal(new[] { h.DeviceA.DeviceId }, DeviceIds(await c.GetDevicesByType("sensor")));
        var stats = Assert.IsType<OkObjectResult>(await c.GetStatistics());
        Assert.Equal(1, JsonSerializer.SerializeToElement(stats.Value).GetProperty("totalDevices").GetInt32());
    }

    [Fact]
    public async Task TenantKey_WithReadScope_CannotChangeAnything()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Read, h.TenantA));
        var deviceId = $"read-key-{Guid.NewGuid():N}";

        AssertStatus(403, await c.RegisterDevice(h.Register(h.TenantA, deviceId)), "register");
        AssertStatus(403, await c.UpdateDevice(h.DeviceA.Id, new UpdateDeviceRequest { Name = "x", IsActive = false }), "update");
        AssertStatus(403, await c.DeactivateDevice(h.DeviceA.Id), "deactivate");

        Assert.False(await h.Db.Devices.AnyAsync(d => d.DeviceId == deviceId));
        var row = await h.Db.Devices.AsNoTracking().FirstAsync(d => d.Id == h.DeviceA.Id);
        Assert.True(row.IsActive);
        Assert.Equal("seeded", row.Name);
    }

    // ----- write scope ------------------------------------------------------------------------

    [Fact]
    public async Task TenantKey_WithWriteScope_ChangesOwnTenant_AndProvisionedByComesFromTheKeyOwner()
    {
        var h = new Harness();
        var keyOwner = Guid.NewGuid();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Write, h.TenantA, keyOwner));
        var request = h.Register(h.TenantA);
        request.ProvisionedByUserId = Guid.NewGuid(); // forged in the body

        var created = Assert.IsType<CreatedAtActionResult>(await c.RegisterDevice(request));
        Assert.False(string.IsNullOrEmpty(JsonSerializer.SerializeToElement(created.Value).GetProperty("sharedSecret").GetString()));
        var row = await h.Db.Devices.AsNoTracking().FirstAsync(d => d.DeviceId == request.DeviceId);
        Assert.Equal(h.TenantA, row.TenantId);
        Assert.Equal(keyOwner, row.ProvisionedByUserId);

        AssertStatus(200, await c.UpdateDevice(h.DeviceA.Id, new UpdateDeviceRequest { Name = "renamed" }), "update own");
        AssertStatus(200, await c.DeactivateDevice(h.DeviceA.Id), "deactivate own");
        Assert.False((await h.Db.Devices.AsNoTracking().FirstAsync(d => d.Id == h.DeviceA.Id)).IsActive);
    }

    [Fact]
    public async Task TenantKey_WithWriteScope_CannotChangeAnotherTenant_NothingStored()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Write, h.TenantA));
        var deviceId = $"intruder-{Guid.NewGuid():N}";

        AssertStatus(403, await c.RegisterDevice(h.Register(h.TenantB, deviceId)), "register foreign");
        AssertStatus(403, await c.UpdateDevice(h.DeviceB.Id, new UpdateDeviceRequest { Name = "pwned" }), "update foreign");
        AssertStatus(403, await c.DeactivateDevice(h.DeviceB.Id), "deactivate foreign");

        Assert.False(await h.Db.Devices.AnyAsync(d => d.DeviceId == deviceId));
        var row = await h.Db.Devices.AsNoTracking().FirstAsync(d => d.Id == h.DeviceB.Id);
        Assert.True(row.IsActive);
        Assert.Equal("seeded", row.Name);
    }

    [Fact]
    public async Task ApiKeyIssuedByAManagerOfAnotherTenant_DoesNotInheritThatUsersRoles()
    {
        var h = new Harness();
        var manager = new User { Id = Guid.NewGuid(), Email = "m@device-key.test", PasswordHash = "x" };
        var role = new Role { Id = Guid.NewGuid(), Name = "BuildingManager", Description = "BuildingManager" };
        h.Db.Users.Add(manager);
        h.Db.Roles.Add(role);
        h.Db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = manager.Id, RoleId = role.Id, TenantId = h.TenantB });
        await h.Db.SaveChangesAsync();
        // The key carries the manager's id as NameIdentifier, but it is a credential of tenant A with its own scope.
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Write, h.TenantA, issuedBy: manager.Id));
        var deviceId = $"via-key-{Guid.NewGuid():N}";

        AssertStatus(403, await c.RegisterDevice(h.Register(h.TenantB, deviceId)), "register in the issuer's tenant");
        AssertStatus(403, await c.DeactivateDevice(h.DeviceB.Id), "deactivate in the issuer's tenant");
        AssertStatus(404, await c.GetDevice(h.DeviceB.Id), "get in the issuer's tenant");
        Assert.False(await h.Db.Devices.AnyAsync(d => d.DeviceId == deviceId));
    }

    // ----- platform keys ----------------------------------------------------------------------

    [Theory]
    [InlineData(ApiKeyScope.Read)]
    [InlineData(ApiKeyScope.Write)]
    public async Task PlatformKey_WithoutAdminScope_ReachesNoTenant(ApiKeyScope scope)
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(scope, tenant: null));

        AssertStatus(403, await c.RegisterDevice(h.Register(h.TenantA)), "register");
        AssertStatus(403, await c.UpdateDevice(h.DeviceA.Id, new UpdateDeviceRequest { Name = "x" }), "update");
        AssertStatus(403, await c.DeactivateDevice(h.DeviceA.Id), "deactivate");
        AssertStatus(404, await c.GetDevice(h.DeviceA.Id), "get");
        AssertStatus(403, await c.GetDevicesByTenant(h.TenantA), "by-tenant");
        Assert.Empty(DeviceIds(await c.GetDevices()));
        Assert.Empty(DeviceIds(await c.GetDevicesByType("sensor")));
        Assert.True((await h.Db.Devices.AsNoTracking().FirstAsync(d => d.Id == h.DeviceA.Id)).IsActive);
    }

    [Fact]
    public async Task PlatformKey_WithAdminScope_ReachesEveryTenant()
    {
        var h = new Harness();
        var c = h.ControllerFor(ApiKeyCaller(ApiKeyScope.Admin, tenant: null));

        Assert.IsType<CreatedAtActionResult>(await c.RegisterDevice(h.Register(h.TenantB)));
        AssertStatus(200, await c.UpdateDevice(h.DeviceA.Id, new UpdateDeviceRequest { Name = "renamed" }), "update");
        AssertStatus(200, await c.DeactivateDevice(h.DeviceB.Id), "deactivate");
        AssertStatus(200, await c.GetDevice(h.DeviceA.Id), "get");
        var all = DeviceIds(await c.GetDevices());
        Assert.Contains(h.DeviceA.DeviceId, all);
        Assert.Contains(h.DeviceB.DeviceId, all);
    }

    [Fact]
    public async Task ApiKeyWithoutAUsableScope_IsRefused()
    {
        var h = new Harness();
        var claims = new[]
        {
            new Claim(ApiKeyClaimTypes.AuthMethod, ApiKeyClaimTypes.AuthMethodApiKey),
            new Claim(ApiKeyClaimTypes.TenantId, h.TenantA.ToString("D")),
        };
        var c = h.ControllerFor(new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyDefaults.AuthenticationType)));

        AssertStatus(403, await c.GetDevices(), "list");
        AssertStatus(403, await c.GetDevice(h.DeviceA.Id), "get");
        AssertStatus(403, await c.RegisterDevice(h.Register(h.TenantA)), "register");
        AssertStatus(403, await c.DeactivateDevice(h.DeviceA.Id), "deactivate");
    }
}
