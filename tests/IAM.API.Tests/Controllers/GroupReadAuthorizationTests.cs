using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5161: the group READ routes (get group, list by tenant, list members, effective permissions) follow the
/// tenant rule of the device endpoints: tenant members and SuperAdmin read; an active member of the group itself
/// reads that one group; everyone else gets the same 403 whether or not the group exists.
/// </summary>
public class GroupReadAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public GroupReadAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private async Task<Guid> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>A user, optionally holding a role (by name) in a tenant.</summary>
    private async Task<Guid> CreateUserAsync(Guid? tenantId = null, string roleName = "User")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@group-read.test", PasswordHash = "x" };
        db.Users.Add(user);

        if (tenantId.HasValue)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
            if (role == null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName, TenantId = tenantId.Value };
                db.Roles.Add(role);
            }

            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id, TenantId = tenantId.Value, GrantedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> CreateGroupAsync(Guid tenantId, params (Guid UserId, bool Active, DateTime? Expires)[] members)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var group = new Group { Id = Guid.NewGuid(), Name = $"Group {Guid.NewGuid():N}", TenantId = tenantId };
        db.Groups.Add(group);
        foreach (var (userId, active, expires) in members)
            db.GroupMemberships.Add(new GroupMembership { GroupId = group.Id, UserId = userId, Role = "member", IsActive = active, ExpiresAt = expires });
        await db.SaveChangesAsync();
        return group.Id;
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, string[]? roles = null, string? tenantClaim = null) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@group-read.test", roles ?? new[] { "User" }, tenantClaim));

    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });

    private static async Task<HttpStatusCode[]> ReadRoutesAsync(HttpClient client, Guid tenantId, Guid groupId, Guid userId) => new[]
    {
        (await client.GetAsync($"/api/groups/{groupId}")).StatusCode,
        (await client.GetAsync($"/api/groups/by-tenant/{tenantId}")).StatusCode,
        (await client.GetAsync($"/api/groups/{groupId}/members")).StatusCode,
        (await client.GetAsync($"/api/groups/{userId}/effective-permissions?tenantId={tenantId}")).StatusCode,
    };

    private static readonly string[] RouteNames = { "get", "by-tenant", "members", "effective-permissions" };

    private static void AssertAll(HttpStatusCode expected, HttpStatusCode[] actual)
    {
        for (var i = 0; i < actual.Length; i++)
            Assert.True(actual[i] == expected, $"{RouteNames[i]} returned {actual[i]}, expected {expected}");
    }

    [Fact]
    public async Task UserWithNoMembershipOfTheTenant_Gets403_OnAllFourReadRoutes()
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var group = await CreateGroupAsync(tenant, (await CreateUserAsync(tenant), true, null));
        var outsider = await CreateUserAsync(other);

        AssertAll(HttpStatusCode.Forbidden, await ReadRoutesAsync(ClientAs(outsider), tenant, group, Guid.NewGuid()));
        // A user with no role anywhere is refused too.
        AssertAll(HttpStatusCode.Forbidden, await ReadRoutesAsync(ClientAs(await CreateUserAsync()), tenant, group, Guid.NewGuid()));
    }

    [Fact]
    public async Task TenantMember_AndSuperAdmin_ReadGroupsWithTheSamePayload()
    {
        var tenant = await CreateTenantAsync();
        var memberId = await CreateUserAsync(tenant);
        var group = await CreateGroupAsync(tenant, (memberId, true, null));

        var asMember = await ClientAs(memberId).GetAsync($"/api/groups/{group}");
        var asAdmin = await SuperAdmin().GetAsync($"/api/groups/{group}");
        Assert.Equal(HttpStatusCode.OK, asMember.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
        Assert.Equal(await asAdmin.Content.ReadAsStringAsync(), await asMember.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await ClientAs(memberId).GetAsync($"/api/groups/by-tenant/{tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(memberId).GetAsync($"/api/groups/{group}/members")).StatusCode);
        var list = await SuperAdmin().GetFromJsonAsync<JsonElement>($"/api/groups/by-tenant/{tenant}");
        Assert.Contains(list.EnumerateArray(), g => g.GetProperty("id").GetGuid() == group);
    }

    [Fact]
    public async Task TokenScopedToAnotherTenant_Gets403_OnAllFourReadRoutes()
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var memberId = await CreateUserAsync(tenant);
        var group = await CreateGroupAsync(tenant, (memberId, true, null));

        // The same user, but the token is scoped to a different tenant: no authority here, not even as group member.
        var client = ClientAs(memberId, tenantClaim: other.ToString());
        AssertAll(HttpStatusCode.Forbidden, await ReadRoutesAsync(client, tenant, group, Guid.NewGuid()));
    }

    [Fact]
    public async Task DeviceAndServiceAccountTokens_Get403_OnAllFourReadRoutes()
    {
        var tenant = await CreateTenantAsync();
        var group = await CreateGroupAsync(tenant, (await CreateUserAsync(tenant), true, null));

        var device = ClientWithToken(TestAuthenticationHelper.GenerateDeviceToken(Guid.NewGuid(), "dev-1", tenant));
        AssertAll(HttpStatusCode.Forbidden, await ReadRoutesAsync(device, tenant, group, Guid.NewGuid()));

        var service = ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "groups:read"));
        AssertAll(HttpStatusCode.Forbidden, await ReadRoutesAsync(service, tenant, group, Guid.NewGuid()));
    }

    [Fact]
    public async Task UnknownGroupId_AndForeignGroup_GiveANonAdminTheSame403_ButSuperAdminSees404ForUnknown()
    {
        var tenant = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var foreign = await CreateGroupAsync(other);
        var caller = ClientAs(await CreateUserAsync(tenant));

        foreach (var suffix in new[] { "", "/members" })
        {
            var unknown = await caller.GetAsync($"/api/groups/{Guid.NewGuid()}{suffix}");
            var foreignGroup = await caller.GetAsync($"/api/groups/{foreign}{suffix}");
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, foreignGroup.StatusCode);
            Assert.Equal(await unknown.Content.ReadAsStringAsync(), await foreignGroup.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SuperAdmin().GetAsync($"/api/groups/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task ActiveGroupMemberWithoutTenantRole_ReadsThatGroupOnly()
    {
        var tenant = await CreateTenantAsync();
        var scimUser = await CreateUserAsync();                       // no tenant role at all
        var group = await CreateGroupAsync(tenant, (scimUser, true, null));
        var otherGroup = await CreateGroupAsync(tenant);
        var client = ClientAs(scimUser);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/groups/{group}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/groups/{group}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/groups/{otherGroup}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/groups/{otherGroup}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/groups/by-tenant/{tenant}")).StatusCode);
    }

    [Fact]
    public async Task InactiveOrExpiredGroupMembership_DoesNotGrantRead()
    {
        var tenant = await CreateTenantAsync();
        var inactive = await CreateUserAsync();
        var expired = await CreateUserAsync();
        var group = await CreateGroupAsync(tenant, (inactive, false, null), (expired, true, DateTime.UtcNow.AddDays(-1)));

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(inactive).GetAsync($"/api/groups/{group}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(expired).GetAsync($"/api/groups/{group}")).StatusCode);
    }

    [Fact]
    public async Task EffectivePermissions_AllowedForSelf_TenantAdmin_AndSuperAdmin_NotForAMember()
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateUserAsync(tenant);
        var colleague = await CreateUserAsync(tenant);                // plain member of the tenant
        var tenantAdmin = await CreateUserAsync(tenant, "TenantAdmin");
        var url = $"/api/groups/{target}/effective-permissions?tenantId={tenant}";

        Assert.Equal(HttpStatusCode.OK, (await ClientAs(target).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(tenantAdmin, new[] { "TenantAdmin" }).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(colleague).GetAsync(url)).StatusCode);

        // A tenant admin of another tenant has no say over this tenant.
        var otherTenant = await CreateTenantAsync();
        var foreignAdmin = await CreateUserAsync(otherTenant, "TenantAdmin");
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(foreignAdmin, new[] { "TenantAdmin" }).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task MyGroups_StillWorksForAnyUser()
    {
        var tenant = await CreateTenantAsync();
        var user = await CreateUserAsync();
        var group = await CreateGroupAsync(tenant, (user, true, null));

        var response = await ClientAs(user).GetAsync("/api/groups/my-groups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var groups = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(groups.EnumerateArray(), g => g.GetProperty("id").GetGuid() == group);
    }
}
