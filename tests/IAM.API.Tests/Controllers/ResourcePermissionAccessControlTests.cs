using System.Net;
using System.Net.Http.Json;
using IAM.API.Controllers;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4717: granting and revoking resource permissions needs ManageAccess on the resource (or an admin
/// role) inside the caller's own tenant. Covers the grant endpoint and all three revoke endpoints:
/// allowed, denied and cross-tenant.
/// </summary>
public class ResourcePermissionAccessControlTests : IClassFixture<IAMTestWebApplicationFactory>, IAsyncLifetime
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IAMTestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ResourcePermissionAccessControlTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        if (await db.Tenants.FindAsync(OtherTenantId) == null)
        {
            db.Tenants.Add(new Tenant { Id = OtherTenantId, Name = "Other Tenant", Slug = "other-tenant", IsActive = true });
            await db.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------- grant

    [Fact]
    public async Task Grant_WithoutAnyPermission_Returns403_AndCreatesNothing()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var attacker = await SeedUserAsync(RootTenantId);

        var response = await GrantAsync(attacker, RootTenantId, ResourceType.Room, h.RoomId, PermissionAction.Control, toUser: attacker);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, CountActivePermissions(ResourceType.Room, h.RoomId));
    }

    [Fact]
    public async Task Grant_WithViewButNotManageAccess_Returns403()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var viewer = await SeedUserAsync(RootTenantId);
        await SeedPermissionAsync(viewer, ResourceType.Room, h.RoomId, PermissionAction.View | PermissionAction.Control, RootTenantId);

        var response = await GrantAsync(viewer, RootTenantId, ResourceType.Room, h.RoomId, PermissionAction.View, toUser: viewer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, CountActivePermissions(ResourceType.Room, h.RoomId)); // only the seeded row
    }

    [Fact]
    public async Task Grant_WithManageAccess_Returns201()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var manager = await SeedUserAsync(RootTenantId);
        var colleague = await SeedUserAsync(RootTenantId);
        await SeedPermissionAsync(manager, ResourceType.Room, h.RoomId, PermissionAction.Manager, RootTenantId);

        var response = await GrantAsync(manager, RootTenantId, ResourceType.Room, h.RoomId, PermissionAction.View, toUser: colleague);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, CountActivePermissions(ResourceType.Room, h.RoomId));
    }

    [Fact]
    public async Task Grant_WithManageAccessInheritedFromBuilding_Returns201()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var manager = await SeedUserAsync(RootTenantId);
        var colleague = await SeedUserAsync(RootTenantId);
        await SeedPermissionAsync(manager, ResourceType.Building, h.BuildingId, PermissionAction.Manager, RootTenantId, inheritToChildren: true);

        var response = await GrantAsync(manager, RootTenantId, ResourceType.IoTDevice, h.DeviceId, PermissionAction.View, toUser: colleague);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Grant_ManageAccessHolder_CannotGrantMoreThanTheyHold()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var manager = await SeedUserAsync(RootTenantId);
        var colleague = await SeedUserAsync(RootTenantId);
        await SeedPermissionAsync(manager, ResourceType.Room, h.RoomId, PermissionAction.View | PermissionAction.ManageAccess, RootTenantId);

        var tooMuch = await GrantAsync(manager, RootTenantId, ResourceType.Room, h.RoomId,
            PermissionAction.View | PermissionAction.Control | PermissionAction.Configure, toUser: colleague);
        Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);
        Assert.Equal(1, CountActivePermissions(ResourceType.Room, h.RoomId)); // only the seeded row

        var withinOwn = await GrantAsync(manager, RootTenantId, ResourceType.Room, h.RoomId,
            PermissionAction.View | PermissionAction.ManageAccess, toUser: colleague);
        Assert.Equal(HttpStatusCode.Created, withinOwn.StatusCode);
    }

    [Fact]
    public async Task Grant_TenantAdmin_CanGrantAnyAction()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var admin = await SeedUserAsync(RootTenantId, tenantRole: "TenantAdmin");
        var colleague = await SeedUserAsync(RootTenantId);

        var response = await GrantAsync(admin, RootTenantId, ResourceType.IoTDevice, h.DeviceId, PermissionAction.FullAccess, toUser: colleague);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Grant_SuperAdmin_CanGrantAnyAction()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var superAdmin = Guid.NewGuid();
        var colleague = await SeedUserAsync(RootTenantId);

        var response = await GrantAsync(superAdmin, RootTenantId, ResourceType.IoTDevice, h.DeviceId, PermissionAction.FullAccess,
            toUser: colleague, roleClaims: new[] { "SuperAdmin" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Grant_AdminRoleClaimWithoutTenantRoleRow_Returns403()
    {
        // Role claims are global names, not tenant-scoped: only an active UserRoles row for the
        // caller's tenant makes someone a tenant admin.
        var h = await SeedHierarchyAsync(RootTenantId);
        var claimOnly = await SeedUserAsync(RootTenantId);

        var response = await GrantAsync(claimOnly, RootTenantId, ResourceType.Room, h.RoomId, PermissionAction.Control,
            toUser: claimOnly, roleClaims: new[] { "TenantAdmin", "SystemAdmin", "BuildingOwner" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, CountActivePermissions(ResourceType.Room, h.RoomId));
    }

    [Fact]
    public async Task Grant_AdminOfAnotherTenant_Returns403()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var otherAdmin = await SeedUserAsync(OtherTenantId, tenantRole: "TenantAdmin");

        // Same person, but the token addresses the root tenant where they hold no role.
        var response = await GrantAsync(otherAdmin, RootTenantId, ResourceType.Room, h.RoomId, PermissionAction.Control, toUser: otherAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, CountActivePermissions(ResourceType.Room, h.RoomId));
    }

    [Fact]
    public async Task Grant_OnResourceOfAnotherTenant_IsRefusedForEveryone()
    {
        var foreign = await SeedHierarchyAsync(OtherTenantId);
        var admin = await SeedUserAsync(RootTenantId, tenantRole: "TenantAdmin");
        var nobody = await SeedUserAsync(RootTenantId);
        var superAdmin = Guid.NewGuid();

        var asAdmin = await GrantAsync(admin, RootTenantId, ResourceType.Room, foreign.RoomId, PermissionAction.Control, toUser: admin);
        var asNobody = await GrantAsync(nobody, RootTenantId, ResourceType.Room, foreign.RoomId, PermissionAction.Control, toUser: nobody);
        var asSuperAdmin = await GrantAsync(superAdmin, RootTenantId, ResourceType.Room, foreign.RoomId, PermissionAction.Control,
            toUser: nobody, roleClaims: new[] { "SuperAdmin" });

        Assert.Equal(HttpStatusCode.NotFound, asAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asNobody.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asSuperAdmin.StatusCode);
        Assert.Equal(0, CountActivePermissions(ResourceType.Room, foreign.RoomId));
    }

    [Fact]
    public async Task Grant_OnResourceThatDoesNotExist_Returns404ForAdmin()
    {
        var admin = await SeedUserAsync(RootTenantId, tenantRole: "TenantAdmin");

        var response = await GrantAsync(admin, RootTenantId, ResourceType.IoTDevice, Guid.NewGuid(), PermissionAction.Control, toUser: admin);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------- revoke by id

    [Fact]
    public async Task RevokeById_ByNonManager_Returns403_AndRowStillExists()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var victim = await SeedUserAsync(RootTenantId);
        var attacker = await SeedUserAsync(RootTenantId);
        var permissionId = await SeedPermissionAsync(victim, ResourceType.Room, h.RoomId, PermissionAction.Control, RootTenantId);
        await SeedPermissionAsync(attacker, ResourceType.Room, h.RoomId, PermissionAction.View, RootTenantId);

        var response = await SendAsync(HttpMethod.Delete, $"/api/resourcepermission/{permissionId}", TokenFor(attacker, RootTenantId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await IsActiveAsync(permissionId));
    }

    [Fact]
    public async Task RevokeById_ByManageAccessHolder_Returns204()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var victim = await SeedUserAsync(RootTenantId);
        var manager = await SeedUserAsync(RootTenantId);
        var permissionId = await SeedPermissionAsync(victim, ResourceType.Room, h.RoomId, PermissionAction.Control, RootTenantId);
        await SeedPermissionAsync(manager, ResourceType.Building, h.BuildingId, PermissionAction.Manager, RootTenantId);

        var response = await SendAsync(HttpMethod.Delete, $"/api/resourcepermission/{permissionId}", TokenFor(manager, RootTenantId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await IsActiveAsync(permissionId));
    }

    [Fact]
    public async Task RevokeById_ByTenantAdmin_Returns204()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var victim = await SeedUserAsync(RootTenantId);
        var admin = await SeedUserAsync(RootTenantId, tenantRole: "BuildingOwner");
        var permissionId = await SeedPermissionAsync(victim, ResourceType.Room, h.RoomId, PermissionAction.Control, RootTenantId);

        var response = await SendAsync(HttpMethod.Delete, $"/api/resourcepermission/{permissionId}", TokenFor(admin, RootTenantId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await IsActiveAsync(permissionId));
    }

    [Fact]
    public async Task RevokeById_PermissionOfAnotherTenant_Returns404_AndRowStillExists()
    {
        var foreign = await SeedHierarchyAsync(OtherTenantId);
        var victim = await SeedUserAsync(OtherTenantId);
        var admin = await SeedUserAsync(RootTenantId, tenantRole: "TenantAdmin");
        var permissionId = await SeedPermissionAsync(victim, ResourceType.Room, foreign.RoomId, PermissionAction.Control, OtherTenantId);

        var response = await SendAsync(HttpMethod.Delete, $"/api/resourcepermission/{permissionId}", TokenFor(admin, RootTenantId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await IsActiveAsync(permissionId));
    }

    // -------------------------------------------------- revoke all (user/role)

    [Fact]
    public async Task RevokeAllUserPermissions_ByNonManager_Returns403_AndRowsRemain()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var victim = await SeedUserAsync(RootTenantId);
        var attacker = await SeedUserAsync(RootTenantId);
        var permissionId = await SeedPermissionAsync(victim, ResourceType.Room, h.RoomId, PermissionAction.Control, RootTenantId);

        var response = await SendAsync(HttpMethod.Delete,
            $"/api/resourcepermission/user/{victim}/resource/{ResourceType.Room}/{h.RoomId}", TokenFor(attacker, RootTenantId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await IsActiveAsync(permissionId));
    }

    [Fact]
    public async Task RevokeAllUserPermissions_ByManageAccessHolder_Returns200WithCount()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var victim = await SeedUserAsync(RootTenantId);
        var manager = await SeedUserAsync(RootTenantId);
        var permissionId = await SeedPermissionAsync(victim, ResourceType.Room, h.RoomId, PermissionAction.Control, RootTenantId);
        await SeedPermissionAsync(manager, ResourceType.Room, h.RoomId, PermissionAction.Manager, RootTenantId);

        var response = await SendAsync(HttpMethod.Delete,
            $"/api/resourcepermission/user/{victim}/resource/{ResourceType.Room}/{h.RoomId}", TokenFor(manager, RootTenantId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await response.Content.ReadFromJsonAsync<int>());
        Assert.False(await IsActiveAsync(permissionId));
    }

    [Fact]
    public async Task RevokeAllUserPermissions_OnResourceOfAnotherTenant_IsRefused()
    {
        var foreign = await SeedHierarchyAsync(OtherTenantId);
        var victim = await SeedUserAsync(OtherTenantId);
        var admin = await SeedUserAsync(RootTenantId, tenantRole: "TenantAdmin");
        var permissionId = await SeedPermissionAsync(victim, ResourceType.Room, foreign.RoomId, PermissionAction.Control, OtherTenantId);

        var response = await SendAsync(HttpMethod.Delete,
            $"/api/resourcepermission/user/{victim}/resource/{ResourceType.Room}/{foreign.RoomId}", TokenFor(admin, RootTenantId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await IsActiveAsync(permissionId));
    }

    [Fact]
    public async Task RevokeAllRolePermissions_ByNonManager_Returns403_AndRowsRemain()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var attacker = await SeedUserAsync(RootTenantId);
        var roleId = Guid.NewGuid();
        var permissionId = await SeedPermissionAsync(null, ResourceType.Room, h.RoomId, PermissionAction.Control, RootTenantId, roleId: roleId);

        var response = await SendAsync(HttpMethod.Delete,
            $"/api/resourcepermission/role/{roleId}/resource/{ResourceType.Room}/{h.RoomId}", TokenFor(attacker, RootTenantId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await IsActiveAsync(permissionId));
    }

    [Fact]
    public async Task RevokeAllRolePermissions_ByTenantAdmin_Returns200WithCount()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var admin = await SeedUserAsync(RootTenantId, tenantRole: "TenantAdmin");
        var roleId = Guid.NewGuid();
        var permissionId = await SeedPermissionAsync(null, ResourceType.Room, h.RoomId, PermissionAction.Control, RootTenantId, roleId: roleId);

        var response = await SendAsync(HttpMethod.Delete,
            $"/api/resourcepermission/role/{roleId}/resource/{ResourceType.Room}/{h.RoomId}", TokenFor(admin, RootTenantId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await response.Content.ReadFromJsonAsync<int>());
        Assert.False(await IsActiveAsync(permissionId));
    }

    [Fact]
    public async Task RevokeAllRolePermissions_OnResourceOfAnotherTenant_IsRefused()
    {
        var foreign = await SeedHierarchyAsync(OtherTenantId);
        var admin = await SeedUserAsync(RootTenantId, tenantRole: "TenantAdmin");
        var roleId = Guid.NewGuid();
        var permissionId = await SeedPermissionAsync(null, ResourceType.Room, foreign.RoomId, PermissionAction.Control, OtherTenantId, roleId: roleId);

        var response = await SendAsync(HttpMethod.Delete,
            $"/api/resourcepermission/role/{roleId}/resource/{ResourceType.Room}/{foreign.RoomId}", TokenFor(admin, RootTenantId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await IsActiveAsync(permissionId));
    }

    // ------------------------------------------------------ reads unchanged

    [Fact]
    public async Task ReadEndpoints_StayOpenToAnyTenantMember()
    {
        var h = await SeedHierarchyAsync(RootTenantId);
        var plainUser = await SeedUserAsync(RootTenantId);
        var token = TokenFor(plainUser, RootTenantId);

        var byResource = await SendAsync(HttpMethod.Get, $"/api/resourcepermission/resource/{ResourceType.Room}/{h.RoomId}", token);
        var check = await SendAsync(HttpMethod.Get,
            $"/api/resourcepermission/check?resourceType={ResourceType.Room}&resourceId={h.RoomId}&action={PermissionAction.View}", token);
        var effective = await SendAsync(HttpMethod.Get,
            $"/api/resourcepermission/effective?resourceType={ResourceType.Room}&resourceId={h.RoomId}", token);

        Assert.Equal(HttpStatusCode.OK, byResource.StatusCode);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal(HttpStatusCode.OK, effective.StatusCode);
    }

    // -------------------------------------------------------------- helpers

    private record Hierarchy(Guid LocationId, Guid BuildingId, Guid FloorId, Guid RoomId, Guid DeviceId);

    private async Task<Hierarchy> SeedHierarchyAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var location = new Location { Name = "Campus", TenantId = tenantId };
        var building = new Building { Name = "Building", LocationId = location.Id, TenantId = tenantId };
        var floor = new Floor { Name = "Floor", BuildingId = building.Id, TenantId = tenantId };
        var room = new Room { Name = "Room", FloorId = floor.Id, TenantId = tenantId };
        var device = new IoTDevice { Name = "Device", DeviceId = $"DEV-{Guid.NewGuid():N}", RoomId = room.Id, TenantId = tenantId };

        db.Locations.Add(location);
        db.Buildings.Add(building);
        db.Floors.Add(floor);
        db.Rooms.Add(room);
        db.IoTDevices.Add(device);
        await db.SaveChangesAsync();

        return new Hierarchy(location.Id, building.Id, floor.Id, room.Id, device.Id);
    }

    /// <summary>A user with no permissions; with <paramref name="tenantRole"/> also an active role row in the tenant.</summary>
    private async Task<Guid> SeedUserAsync(Guid tenantId, string? tenantRole = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var user = new User { Email = $"{Guid.NewGuid():N}@test.local", PasswordHash = "x" };
        db.Users.Add(user);

        if (tenantRole != null)
        {
            var role = new Role { Name = tenantRole, TenantId = tenantId, Description = tenantRole };
            db.Roles.Add(role);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, TenantId = tenantId });
        }

        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedPermissionAsync(
        Guid? userId,
        ResourceType type,
        Guid resourceId,
        PermissionAction actions,
        Guid tenantId,
        bool inheritToChildren = true,
        Guid? roleId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var permission = new ResourcePermission
        {
            UserId = userId,
            RoleId = roleId,
            ResourceType = type,
            ResourceId = resourceId,
            Actions = actions,
            TenantId = tenantId,
            InheritToChildren = inheritToChildren
        };
        db.ResourcePermissions.Add(permission);
        await db.SaveChangesAsync();
        return permission.Id;
    }

    private int CountActivePermissions(ResourceType type, Guid resourceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return db.ResourcePermissions.Count(p => p.ResourceType == type && p.ResourceId == resourceId && p.IsActive);
    }

    private async Task<bool> IsActiveAsync(Guid permissionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var row = await db.ResourcePermissions.FindAsync(permissionId);
        Assert.NotNull(row);
        return row!.IsActive;
    }

    private static string TokenFor(Guid userId, Guid tenantId, params string[] roleClaims) =>
        TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId}@test.local", roleClaims, tenantId.ToString());

    private Task<HttpResponseMessage> GrantAsync(
        Guid callerId,
        Guid callerTenantId,
        ResourceType type,
        Guid resourceId,
        PermissionAction actions,
        Guid toUser,
        string[]? roleClaims = null)
    {
        var request = new GrantPermissionRequest
        {
            UserId = toUser,
            ResourceType = type,
            ResourceId = resourceId,
            Actions = actions,
            InheritToChildren = true
        };
        return SendAsync(HttpMethod.Post, "/api/resourcepermission/grant", TokenFor(callerId, callerTenantId, roleClaims ?? Array.Empty<string>()), request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        var message = new HttpRequestMessage(method, url);
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            message.Content = JsonContent.Create(body);
        return await _client.SendAsync(message);
    }
}
