using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5160: all 13 network-policy routes (IP allowlist, geo restriction, geofences, blocked-IP log, IP check) need a
/// global administrator (SuperAdmin/SystemAdmin) or an active TenantAdmin/BuildingOwner/BuildingManager row in the
/// addressed tenant. Routes addressed by an entry id decide from the stored entry's tenant, and a caller who may not
/// act gets the same 403 for a foreign and for an unknown id.
/// </summary>
public class NetworkPolicyAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IAMTestWebApplicationFactory _factory;

    public NetworkPolicyAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------

    private sealed record Tenant5160(Guid Id, Guid AllowlistId, Guid FenceId);

    /// <summary>A tenant with one allowlist entry, one geofence, a geo restriction and a blocked-log row.</summary>
    private async Task<Tenant5160> SeedTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        var entry = new IpAllowlistEntry { TenantId = tenant.Id, Cidr = "10.20.0.0/16", Description = "office" };
        var fence = new GeoFence { TenantId = tenant.Id, Name = "HQ", Latitude = 52.1, Longitude = 5.1, RadiusMeters = 500 };
        db.Tenants.Add(tenant);
        db.IpAllowlistEntries.Add(entry);
        db.GeoFences.Add(fence);
        db.GeoRestrictions.Add(new GeoRestriction { TenantId = tenant.Id, AllowedCountries = "[\"NL\"]", IsActive = true });
        db.BlockedIpLogs.Add(new BlockedIpLog { TenantId = tenant.Id, IpAddress = "203.0.113.9", Reason = "not allowlisted" });
        await db.SaveChangesAsync();
        return new Tenant5160(tenant.Id, entry.Id, fence.Id);
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@netpol-authz.test", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task GrantRoleAsync(Guid userId, string roleName, Guid? tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName, TenantId = RootTenantId };
            db.Roles.Add(role);
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenantId,
            GrantedAt = DateTime.UtcNow, ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@netpol-authz.test", roles, tenantClaim));

    private async Task<(Guid UserId, HttpClient Client)> UserWithRoleAsync(string roleName, Guid? tenantId,
        DateTime? expiresAt = null, string? tenantClaim = null)
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId, expiresAt);
        return (userId, ClientAs(userId, new[] { roleName }, tenantClaim));
    }

    private async Task<HttpClient> GlobalAdminAsync(string role) => ClientAs(await CreateUserAsync(), new[] { role });

    private static readonly string[] RouteNames =
    {
        "allowlist-get", "allowlist-create", "allowlist-update", "allowlist-delete",
        "geo-get", "geo-upsert", "geo-delete",
        "fences-get", "fences-create", "fences-update", "fences-delete",
        "blocked-log", "check-ip"
    };

    /// <summary>All 13 routes aimed at the tenant (and its entry ids); order matches <see cref="RouteNames"/>.</summary>
    private static async Task<HttpStatusCode[]> AllRoutesAsync(HttpClient c, Guid tenantId, Guid allowlistId, Guid fenceId) => new[]
    {
        (await c.GetAsync($"/api/NetworkPolicy/ip-allowlist?tenantId={tenantId}")).StatusCode,
        (await c.PostAsJsonAsync("/api/NetworkPolicy/ip-allowlist", new { tenantId, cidr = "192.0.2.0/24", description = "new" })).StatusCode,
        (await c.PutAsJsonAsync($"/api/NetworkPolicy/ip-allowlist/{allowlistId}", new { cidr = "10.99.0.0/16", description = "changed", isActive = true })).StatusCode,
        (await c.DeleteAsync($"/api/NetworkPolicy/ip-allowlist/{allowlistId}")).StatusCode,
        (await c.GetAsync($"/api/NetworkPolicy/geo-restriction?tenantId={tenantId}")).StatusCode,
        (await c.PutAsJsonAsync("/api/NetworkPolicy/geo-restriction", new { tenantId, allowedCountries = "[\"DE\"]", isActive = true })).StatusCode,
        (await c.DeleteAsync($"/api/NetworkPolicy/geo-restriction?tenantId={tenantId}")).StatusCode,
        (await c.GetAsync($"/api/NetworkPolicy/geofences?tenantId={tenantId}")).StatusCode,
        (await c.PostAsJsonAsync("/api/NetworkPolicy/geofences", new { tenantId, name = "New", latitude = 52.0, longitude = 5.0, radiusMeters = 100 })).StatusCode,
        (await c.PutAsJsonAsync($"/api/NetworkPolicy/geofences/{fenceId}", new { name = "Moved", latitude = 1.0, longitude = 2.0, radiusMeters = 50, isActive = true })).StatusCode,
        (await c.DeleteAsync($"/api/NetworkPolicy/geofences/{fenceId}")).StatusCode,
        (await c.GetAsync($"/api/NetworkPolicy/blocked-log?tenantId={tenantId}")).StatusCode,
        (await c.PostAsJsonAsync("/api/NetworkPolicy/check-ip", new { tenantId, ipAddress = "8.8.8.8" })).StatusCode,
    };

    private static void AssertAll(HttpStatusCode expected, HttpStatusCode[] statuses)
    {
        for (var i = 0; i < statuses.Length; i++)
            Assert.True(statuses[i] == expected, $"{RouteNames[i]} returned {statuses[i]}, expected {expected}");
    }

    private static void AssertAllSucceeded(HttpStatusCode[] statuses)
    {
        for (var i = 0; i < statuses.Length; i++)
            Assert.True(statuses[i] is HttpStatusCode.OK or HttpStatusCode.NoContent, $"{RouteNames[i]} returned {statuses[i]}");
    }

    private async Task AssertTenantUntouchedAsync(Tenant5160 t)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var entries = await db.IpAllowlistEntries.AsNoTracking().Where(e => e.TenantId == t.Id).ToListAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(t.AllowlistId, entry.Id);
        Assert.Equal("10.20.0.0/16", entry.Cidr);
        Assert.Equal("office", entry.Description);
        var fence = Assert.Single(await db.GeoFences.AsNoTracking().Where(f => f.TenantId == t.Id).ToListAsync());
        Assert.Equal("HQ", fence.Name);
        Assert.Equal(52.1, fence.Latitude);
        var geo = await db.GeoRestrictions.AsNoTracking().SingleAsync(g => g.TenantId == t.Id);
        Assert.Equal("[\"NL\"]", geo.AllowedCountries);
    }

    // ----- refused callers ---------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_Gets401_OnAll13Routes()
    {
        var t = await SeedTenantAsync();

        AssertAll(HttpStatusCode.Unauthorized, await AllRoutesAsync(_factory.CreateClient(), t.Id, t.AllowlistId, t.FenceId));
    }

    [Fact]
    public async Task UserWithoutAnyRole_Gets403_OnAll13Routes_AndNothingChanges()
    {
        var t = await SeedTenantAsync();
        var client = ClientAs(await CreateUserAsync(), new[] { "User" });

        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(client, t.Id, t.AllowlistId, t.FenceId));
        await AssertTenantUntouchedAsync(t);
    }

    [Fact]
    public async Task PlainMemberOfTheTenant_Gets403_OnAll13Routes()
    {
        var t = await SeedTenantAsync();
        var (_, client) = await UserWithRoleAsync("User", t.Id);

        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(client, t.Id, t.AllowlistId, t.FenceId));
        await AssertTenantUntouchedAsync(t);
    }

    [Fact]
    public async Task AdminRoleClaimWithoutARoleRow_GrantsNothing()
    {
        var t = await SeedTenantAsync();
        var client = ClientAs(await CreateUserAsync(), new[] { "TenantAdmin", "BuildingManager", "BuildingOwner" });

        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(client, t.Id, t.AllowlistId, t.FenceId));
        await AssertTenantUntouchedAsync(t);
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    public async Task AdminOfAnotherTenant_Gets403_OnAll13Routes_AndNothingChanges(string role)
    {
        var victim = await SeedTenantAsync();
        var own = await SeedTenantAsync();
        var (_, client) = await UserWithRoleAsync(role, own.Id);

        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(client, victim.Id, victim.AllowlistId, victim.FenceId));
        await AssertTenantUntouchedAsync(victim);
    }

    [Fact]
    public async Task ExpiredAdminRole_Gets403()
    {
        var t = await SeedTenantAsync();
        var (_, client) = await UserWithRoleAsync("TenantAdmin", t.Id, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(client, t.Id, t.AllowlistId, t.FenceId));
        await AssertTenantUntouchedAsync(t);
    }

    [Fact]
    public async Task AdminRoleWithoutATenant_GrantsNothing()
    {
        var t = await SeedTenantAsync();
        var (_, client) = await UserWithRoleAsync("TenantAdmin", tenantId: null);

        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(client, t.Id, t.AllowlistId, t.FenceId));
    }

    [Fact]
    public async Task TokenPinnedToAnotherTenant_Gets403_EvenForAnAdminOfBothTenants()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        var (userId, _) = await UserWithRoleAsync("TenantAdmin", a.Id);
        await GrantRoleAsync(userId, "TenantAdmin", b.Id);
        var pinnedToA = ClientAs(userId, new[] { "TenantAdmin" }, tenantClaim: a.Id.ToString());

        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(pinnedToA, b.Id, b.AllowlistId, b.FenceId));
        await AssertTenantUntouchedAsync(b);
        AssertAllSucceeded(await AllRoutesAsync(pinnedToA, a.Id, a.AllowlistId, a.FenceId));
    }

    [Fact]
    public async Task DeviceAndServiceAccountTokens_Get403()
    {
        var t = await SeedTenantAsync();
        var device = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(Guid.NewGuid(), "dev-1", t.Id));
        var service = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "network:write"));

        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(device, t.Id, t.AllowlistId, t.FenceId));
        AssertAll(HttpStatusCode.Forbidden, await AllRoutesAsync(service, t.Id, t.AllowlistId, t.FenceId));
        await AssertTenantUntouchedAsync(t);
    }

    // ----- allowed callers ---------------------------------------------------------------------

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    public async Task TenantAdministrator_CanUseAll13Routes_OfTheirOwnTenant(string role)
    {
        var t = await SeedTenantAsync();
        var (_, client) = await UserWithRoleAsync(role, t.Id);

        AssertAllSucceeded(await AllRoutesAsync(client, t.Id, t.AllowlistId, t.FenceId));
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task GlobalAdministrator_CanUseAll13Routes_OfAnyTenant(string role)
    {
        var t = await SeedTenantAsync();
        var client = await GlobalAdminAsync(role);

        AssertAllSucceeded(await AllRoutesAsync(client, t.Id, t.AllowlistId, t.FenceId));
    }

    [Fact]
    public async Task GlobalAdministrator_GetsTheOldNotFound_ForAnUnknownEntryId()
    {
        var client = await GlobalAdminAsync("SuperAdmin");

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/NetworkPolicy/ip-allowlist/{Guid.NewGuid()}",
            new { cidr = "10.0.0.0/8", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/NetworkPolicy/geofences/{Guid.NewGuid()}")).StatusCode);
    }

    // ----- id-only routes ----------------------------------------------------------------------

    [Fact]
    public async Task UpdateAndDeleteById_AreRefused_WhenTheEntryBelongsToATenantTheCallerDoesNotAdminister()
    {
        var victim = await SeedTenantAsync();
        var own = await SeedTenantAsync();
        var (_, client) = await UserWithRoleAsync("TenantAdmin", own.Id);

        // The body names the caller's own tenant, which must not matter: the tenant comes from the stored entry.
        var updateEntry = await client.PutAsJsonAsync($"/api/NetworkPolicy/ip-allowlist/{victim.AllowlistId}",
            new { tenantId = own.Id, cidr = "0.0.0.0/0", description = "open", isActive = true });
        var deleteEntry = await client.DeleteAsync($"/api/NetworkPolicy/ip-allowlist/{victim.AllowlistId}");
        var updateFence = await client.PutAsJsonAsync($"/api/NetworkPolicy/geofences/{victim.FenceId}",
            new { tenantId = own.Id, name = "pwned", latitude = 0, longitude = 0, radiusMeters = 1, isActive = false });
        var deleteFence = await client.DeleteAsync($"/api/NetworkPolicy/geofences/{victim.FenceId}");

        Assert.All(new[] { updateEntry, deleteEntry, updateFence, deleteFence }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        await AssertTenantUntouchedAsync(victim);
    }

    [Fact]
    public async Task UnknownEntryIds_GiveANonAdminTheSame403_AsForeignIds()
    {
        var victim = await SeedTenantAsync();
        var own = await SeedTenantAsync();
        var (_, foreignAdmin) = await UserWithRoleAsync("TenantAdmin", own.Id);
        var plain = ClientAs(await CreateUserAsync(), new[] { "User" });

        foreach (var client in new[] { foreignAdmin, plain })
        {
            var foreign = await client.DeleteAsync($"/api/NetworkPolicy/ip-allowlist/{victim.AllowlistId}");
            var unknown = await client.DeleteAsync($"/api/NetworkPolicy/ip-allowlist/{Guid.NewGuid()}");
            var foreignFence = await client.PutAsJsonAsync($"/api/NetworkPolicy/geofences/{victim.FenceId}",
                new { name = "x", latitude = 1, longitude = 1, radiusMeters = 1, isActive = true });
            var unknownFence = await client.PutAsJsonAsync($"/api/NetworkPolicy/geofences/{Guid.NewGuid()}",
                new { name = "x", latitude = 1, longitude = 1, radiusMeters = 1, isActive = true });

            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            Assert.Equal(await foreign.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, foreignFence.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, unknownFence.StatusCode);
            Assert.Equal(await foreignFence.Content.ReadAsStringAsync(), await unknownFence.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task UnknownTenantIds_GiveANonAdminTheSame403_AsExistingTenants()
    {
        var existing = await SeedTenantAsync();
        var (_, client) = await UserWithRoleAsync("TenantAdmin", (await SeedTenantAsync()).Id);

        var known = await client.GetAsync($"/api/NetworkPolicy/ip-allowlist?tenantId={existing.Id}");
        var unknown = await client.GetAsync($"/api/NetworkPolicy/ip-allowlist?tenantId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, known.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnEntryCannotBeMovedToAnotherTenant_ThroughTheRequestBody()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        var (_, adminOfA) = await UserWithRoleAsync("TenantAdmin", a.Id);

        // Update by id with another tenant in the body: allowed (own entry) but the tenant stays.
        var update = await adminOfA.PutAsJsonAsync($"/api/NetworkPolicy/ip-allowlist/{a.AllowlistId}",
            new { tenantId = b.Id, cidr = "10.30.0.0/16", description = "x", isActive = true });
        var fence = await adminOfA.PutAsJsonAsync($"/api/NetworkPolicy/geofences/{a.FenceId}",
            new { tenantId = b.Id, name = "HQ2", latitude = 52.1, longitude = 5.1, radiusMeters = 500, isActive = true });
        // Create naming the other tenant is refused and creates nothing there.
        var create = await adminOfA.PostAsJsonAsync("/api/NetworkPolicy/ip-allowlist", new { tenantId = b.Id, cidr = "192.0.2.0/24" });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fence.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.Equal(a.Id, (await db.IpAllowlistEntries.AsNoTracking().SingleAsync(e => e.Id == a.AllowlistId)).TenantId);
        Assert.Equal(a.Id, (await db.GeoFences.AsNoTracking().SingleAsync(f => f.Id == a.FenceId)).TenantId);
        Assert.Single(await db.IpAllowlistEntries.AsNoTracking().Where(e => e.TenantId == b.Id).ToListAsync());
    }
}
