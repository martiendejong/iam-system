using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4056: group roles (owner/admin/member) must be ENFORCED, not just stored.
/// - the creator becomes owner (same save); creating in a tenant needs membership of it or global admin;
/// - update/delete/add-remove member/role change/permission-role changes are 403 for everyone who is
///   not group owner/admin or global admin (so always for a caller scoped to another tenant);
/// - only owners/global admins grant or remove owner/admin or delete; no self-promotion; the last
///   owner can never be removed or demoted; role values are validated;
/// - the backfill is idempotent, removes nothing, promotes nobody; ownerless groups are listed for
///   global admins only.
/// </summary>
public class GroupRoleEnforcementTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SeededUserRoleId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly IAMTestWebApplicationFactory _factory;

    public GroupRoleEnforcementTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<User> CreateUserAsync(Guid? memberOfTenant = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@grouprole.test",
            PasswordHash = "not-used"
        };
        db.Users.Add(user);

        if (memberOfTenant.HasValue)
        {
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RoleId = SeededUserRoleId,
                TenantId = memberOfTenant.Value,
                GrantedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Tenant> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<Group> CreateGroupWithMembersAsync(Guid tenantId, params (Guid UserId, string Role)[] members)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var group = new Group { Id = Guid.NewGuid(), Name = $"Group {Guid.NewGuid():N}", TenantId = tenantId };
        db.Groups.Add(group);
        foreach (var (userId, role) in members)
        {
            db.GroupMemberships.Add(new GroupMembership { GroupId = group.Id, UserId = userId, Role = role });
        }

        await db.SaveChangesAsync();
        return group;
    }

    /// <summary>
    /// Client authenticated as <paramref name="user"/>. Default token shape = password login:
    /// NO tenant_id claim. Pass <paramref name="tenantClaim"/> for a tenant-scoped token.
    /// </summary>
    private HttpClient ClientAs(User user, string[]? roles = null, string? tenantClaim = null)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(user.Id, user.Email, roles ?? new[] { "User" }, tenantClaim);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<GroupMembership?> GetMembershipAsync(Guid groupId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.GroupMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId);
    }

    private static object CreateGroupBody(Guid tenantId) => new { name = $"api-group-{Guid.NewGuid():N}", tenantId };

    // ----- create: creator becomes owner, tenant membership required --------------------------

    [Fact]
    public async Task Create_TenantMember_Succeeds_AndCreatorBecomesOwnerInSameSave()
    {
        var user = await CreateUserAsync(memberOfTenant: RootTenantId);
        var response = await ClientAs(user).PostAsJsonAsync("/api/groups", CreateGroupBody(RootTenantId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = body.GetProperty("id").GetGuid();

        var membership = await GetMembershipAsync(groupId, user.Id);
        Assert.NotNull(membership);
        Assert.Equal("owner", membership!.Role);
        Assert.True(membership.IsActive);
    }

    [Fact]
    public async Task Create_WithoutTenantMembership_IsForbidden()
    {
        var outsider = await CreateUserAsync(memberOfTenant: null);
        var response = await ClientAs(outsider).PostAsJsonAsync("/api/groups", CreateGroupBody(RootTenantId));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_MemberOfAnotherTenantOnly_IsForbidden()
    {
        var otherTenant = await CreateTenantAsync();
        var user = await CreateUserAsync(memberOfTenant: otherTenant.Id);
        var response = await ClientAs(user).PostAsJsonAsync("/api/groups", CreateGroupBody(RootTenantId));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_BySuperAdmin_WithoutTenantMembership_Succeeds_AndSuperAdminBecomesOwner()
    {
        var admin = await CreateUserAsync(memberOfTenant: null);
        var response = await ClientAs(admin, roles: new[] { "SuperAdmin" })
            .PostAsJsonAsync("/api/groups", CreateGroupBody(RootTenantId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var groupId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal("owner", (await GetMembershipAsync(groupId, admin.Id))!.Role);
    }

    [Fact]
    public async Task Create_TokenScopedToAnotherTenant_IsForbidden_EvenWithTenantMembership()
    {
        var otherTenant = await CreateTenantAsync();
        var user = await CreateUserAsync(memberOfTenant: otherTenant.Id);
        // tenant_id claim points at the root tenant, target is otherTenant: must be refused
        var response = await ClientAs(user, tenantClaim: RootTenantId.ToString())
            .PostAsJsonAsync("/api/groups", CreateGroupBody(otherTenant.Id));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- update ----------------------------------------------------------------------------

    [Fact]
    public async Task Update_ByOutsiderAndByPlainMember_IsForbidden()
    {
        var owner = await CreateUserAsync();
        var member = await CreateUserAsync();
        var outsider = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (member.Id, "member"));

        var body = new { name = "renamed" };
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(outsider).PutAsJsonAsync($"/api/groups/{group.Id}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(member).PutAsJsonAsync($"/api/groups/{group.Id}", body)).StatusCode);
    }

    [Fact]
    public async Task Update_ByGroupAdmin_ByOwner_AndBySuperAdmin_Succeeds()
    {
        var owner = await CreateUserAsync();
        var admin = await CreateUserAsync();
        var superAdmin = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (admin.Id, "admin"));

        Assert.Equal(HttpStatusCode.OK, (await ClientAs(admin).PutAsJsonAsync($"/api/groups/{group.Id}", new { name = "by admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(owner).PutAsJsonAsync($"/api/groups/{group.Id}", new { name = "by owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).PutAsJsonAsync($"/api/groups/{group.Id}", new { name = "by superadmin" })).StatusCode);
    }

    [Fact]
    public async Task Update_WithTokenScopedToAnotherTenant_IsForbidden_EvenForTheGroupOwner()
    {
        var otherTenant = await CreateTenantAsync();
        var owner = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(otherTenant.Id, (owner.Id, "owner"));

        // Same user, but the token is scoped to the root tenant, not the group's tenant.
        var response = await ClientAs(owner, tenantClaim: RootTenantId.ToString())
            .PutAsJsonAsync($"/api/groups/{group.Id}", new { name = "cross-tenant" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Control: the password-login shaped token (no tenant claim) IS honoured.
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(owner).PutAsJsonAsync($"/api/groups/{group.Id}", new { name = "same-tenant" })).StatusCode);
    }

    // ----- delete ----------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ByGroupAdmin_IsForbidden_OwnerOnly()
    {
        var owner = await CreateUserAsync();
        var admin = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (admin.Id, "admin"));

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(admin).DeleteAsync($"/api/groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(owner).DeleteAsync($"/api/groups/{group.Id}")).StatusCode);
    }

    [Fact]
    public async Task Delete_BySuperAdmin_Succeeds()
    {
        var owner = await CreateUserAsync();
        var superAdmin = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"));

        var response = await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).DeleteAsync($"/api/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----- add member / role change (add-member upserts the role) ----------------------------

    [Fact]
    public async Task AddMember_ByPlainMemberOrOutsider_IsForbidden()
    {
        var owner = await CreateUserAsync();
        var member = await CreateUserAsync();
        var outsider = await CreateUserAsync();
        var newcomer = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (member.Id, "member"));

        var body = new { userId = newcomer.Id, role = "member" };
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(member).PostAsJsonAsync($"/api/groups/{group.Id}/members", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(outsider).PostAsJsonAsync($"/api/groups/{group.Id}/members", body)).StatusCode);
    }

    [Fact]
    public async Task AddMember_GrantMember_ByGroupAdmin_Succeeds()
    {
        var owner = await CreateUserAsync();
        var admin = await CreateUserAsync();
        var newcomer = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (admin.Id, "admin"));

        var response = await ClientAs(admin).PostAsJsonAsync($"/api/groups/{group.Id}/members", new { userId = newcomer.Id, role = "member" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("member", (await GetMembershipAsync(group.Id, newcomer.Id))!.Role);
    }

    [Fact]
    public async Task AddMember_GrantAdminOrOwner_ByGroupAdmin_IsForbidden_OwnerOnly()
    {
        var owner = await CreateUserAsync();
        var admin = await CreateUserAsync();
        var newcomer = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (admin.Id, "admin"));

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(admin).PostAsJsonAsync($"/api/groups/{group.Id}/members", new { userId = newcomer.Id, role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(admin).PostAsJsonAsync($"/api/groups/{group.Id}/members", new { userId = newcomer.Id, role = "owner" })).StatusCode);

        // ... while the owner can grant both.
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(owner).PostAsJsonAsync($"/api/groups/{group.Id}/members", new { userId = newcomer.Id, role = "admin" })).StatusCode);
        Assert.Equal("admin", (await GetMembershipAsync(group.Id, newcomer.Id))!.Role);
    }

    [Fact]
    public async Task AddMember_MemberCannotSelfPromote_ByReAddingThemselves()
    {
        var owner = await CreateUserAsync();
        var member = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (member.Id, "member"));

        var response = await ClientAs(member).PostAsJsonAsync($"/api/groups/{group.Id}/members", new { userId = member.Id, role = "owner" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("member", (await GetMembershipAsync(group.Id, member.Id))!.Role);
    }

    [Fact]
    public async Task AddMember_DemoteAdmin_ByAnotherGroupAdmin_IsForbidden_ButOwnerCan()
    {
        var owner = await CreateUserAsync();
        var admin1 = await CreateUserAsync();
        var admin2 = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (admin1.Id, "admin"), (admin2.Id, "admin"));

        // add-member upserts the role, so this is the demotion path
        var body = new { userId = admin2.Id, role = "member" };
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(admin1).PostAsJsonAsync($"/api/groups/{group.Id}/members", body)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ClientAs(owner).PostAsJsonAsync($"/api/groups/{group.Id}/members", body)).StatusCode);
        Assert.Equal("member", (await GetMembershipAsync(group.Id, admin2.Id))!.Role);
    }

    [Fact]
    public async Task AddMember_InvalidRoleValue_IsRejected()
    {
        var owner = await CreateUserAsync();
        var newcomer = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"));

        var response = await ClientAs(owner).PostAsJsonAsync($"/api/groups/{group.Id}/members", new { userId = newcomer.Id, role = "superowner" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await GetMembershipAsync(group.Id, newcomer.Id));
    }

    // ----- remove member ----------------------------------------------------------------------

    [Fact]
    public async Task RemoveMember_PlainMember_ByGroupAdmin_Succeeds_ButByMember_IsForbidden()
    {
        var owner = await CreateUserAsync();
        var admin = await CreateUserAsync();
        var member1 = await CreateUserAsync();
        var member2 = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId,
            (owner.Id, "owner"), (admin.Id, "admin"), (member1.Id, "member"), (member2.Id, "member"));

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(member1).DeleteAsync($"/api/groups/{group.Id}/members/{member2.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(admin).DeleteAsync($"/api/groups/{group.Id}/members/{member2.Id}")).StatusCode);
        Assert.False((await GetMembershipAsync(group.Id, member2.Id))!.IsActive);
    }

    [Fact]
    public async Task RemoveMember_AdminOrOwner_ByGroupAdmin_IsForbidden_OwnerOnly()
    {
        var owner1 = await CreateUserAsync();
        var owner2 = await CreateUserAsync();
        var admin1 = await CreateUserAsync();
        var admin2 = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId,
            (owner1.Id, "owner"), (owner2.Id, "owner"), (admin1.Id, "admin"), (admin2.Id, "admin"));

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(admin1).DeleteAsync($"/api/groups/{group.Id}/members/{admin2.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(admin1).DeleteAsync($"/api/groups/{group.Id}/members/{owner2.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ClientAs(owner1).DeleteAsync($"/api/groups/{group.Id}/members/{admin2.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(owner1).DeleteAsync($"/api/groups/{group.Id}/members/{owner2.Id}")).StatusCode);
    }

    // ----- last-owner protection ---------------------------------------------------------------

    [Fact]
    public async Task LastOwner_CannotBeRemoved_NotEvenBySuperAdmin()
    {
        var owner = await CreateUserAsync();
        var superAdmin = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"));

        Assert.Equal(HttpStatusCode.BadRequest, (await ClientAs(owner).DeleteAsync($"/api/groups/{group.Id}/members/{owner.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).DeleteAsync($"/api/groups/{group.Id}/members/{owner.Id}")).StatusCode);
        Assert.True((await GetMembershipAsync(group.Id, owner.Id))!.IsActive);
    }

    [Fact]
    public async Task LastOwner_CannotBeDemoted_NotEvenBySuperAdmin_ButWithASecondOwnerItWorks()
    {
        var owner = await CreateUserAsync();
        var superAdmin = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"));
        var superClient = ClientAs(superAdmin, roles: new[] { "SuperAdmin" });

        var demote = new { userId = owner.Id, role = "member" };
        Assert.Equal(HttpStatusCode.BadRequest, (await superClient.PostAsJsonAsync($"/api/groups/{group.Id}/members", demote)).StatusCode);
        Assert.Equal("owner", (await GetMembershipAsync(group.Id, owner.Id))!.Role);

        // With a second owner in place the same demotion is allowed.
        var secondOwner = await CreateUserAsync();
        Assert.Equal(HttpStatusCode.OK, (await superClient.PostAsJsonAsync($"/api/groups/{group.Id}/members", new { userId = secondOwner.Id, role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superClient.PostAsJsonAsync($"/api/groups/{group.Id}/members", demote)).StatusCode);
        Assert.Equal("member", (await GetMembershipAsync(group.Id, owner.Id))!.Role);
    }

    // ----- permission roles ---------------------------------------------------------------------

    [Fact]
    public async Task AssignPermissionRole_ByGroupOwner_IsForbidden_GlobalAdminOnly()
    {
        var owner = await CreateUserAsync();
        var superAdmin = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"));
        var body = new { roleId = SeededUserRoleId, tenantId = RootTenantId };

        // A group owner attaching a permission role to their own group would be privilege escalation.
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(owner).PostAsJsonAsync($"/api/groups/{group.Id}/roles", body)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).PostAsJsonAsync($"/api/groups/{group.Id}/roles", body)).StatusCode);
    }

    [Fact]
    public async Task RemovePermissionRole_ByGroupAdmin_Succeeds_ButByMember_IsForbidden()
    {
        var owner = await CreateUserAsync();
        var admin = await CreateUserAsync();
        var member = await CreateUserAsync();
        var superAdmin = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (owner.Id, "owner"), (admin.Id, "admin"), (member.Id, "member"));

        var attach = new { roleId = SeededUserRoleId, tenantId = RootTenantId };
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).PostAsJsonAsync($"/api/groups/{group.Id}/roles", attach)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(member).DeleteAsync($"/api/groups/{group.Id}/roles/{SeededUserRoleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientAs(admin).DeleteAsync($"/api/groups/{group.Id}/roles/{SeededUserRoleId}")).StatusCode);
    }

    // ----- backfill + ownerless listing ---------------------------------------------------------

    [Fact]
    public async Task Backfill_IsIdempotent_NormalizesCasingOnly_RemovesNothing_PromotesNobody()
    {
        var casedOwner = await CreateUserAsync();
        var legacyMember = await CreateUserAsync();
        var group = await CreateGroupWithMembersAsync(RootTenantId, (casedOwner.Id, "Owner"), (legacyMember.Id, "TeamLead"));
        var ownerless = await CreateGroupWithMembersAsync(RootTenantId, ((await CreateUserAsync()).Id, "member"));

        using var scope = _factory.Services.CreateScope();
        var groupService = scope.ServiceProvider.GetRequiredService<IGroupService>();

        var first = await groupService.BackfillGroupRolesAsync();
        Assert.True(first.NormalizedRoleValues >= 1);
        Assert.True(first.GroupsWithoutActiveOwner >= 1);

        // "Owner" was canonicalized to "owner" (same person, same right), the unknown legacy
        // value is left untouched (no promotion, no removal), and both rows are still active.
        var ownerRow = await GetMembershipAsync(group.Id, casedOwner.Id);
        Assert.Equal("owner", ownerRow!.Role);
        Assert.True(ownerRow.IsActive);
        var legacyRow = await GetMembershipAsync(group.Id, legacyMember.Id);
        Assert.Equal("TeamLead", legacyRow!.Role);
        Assert.True(legacyRow.IsActive);

        // Idempotent: the second run changes nothing.
        var second = await groupService.BackfillGroupRolesAsync();
        Assert.Equal(0, second.NormalizedRoleValues);

        // The ownerless group is reported; the group with a (normalized) owner is not.
        var ownerlessGroups = await groupService.GetGroupsWithoutActiveOwnerAsync();
        Assert.Contains(ownerlessGroups, g => g.Id == ownerless.Id);
        Assert.DoesNotContain(ownerlessGroups, g => g.Id == group.Id);
    }

    [Fact]
    public async Task OwnerlessGroups_Endpoint_IsGlobalAdminOnly_AndListsGroupsWithoutActiveOwner()
    {
        var regular = await CreateUserAsync(memberOfTenant: RootTenantId);
        var superAdmin = await CreateUserAsync();
        var ownerlessGroup = await CreateGroupWithMembersAsync(RootTenantId, ((await CreateUserAsync()).Id, "member"));

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(regular).GetAsync("/api/groups/ownerless")).StatusCode);

        var response = await ClientAs(superAdmin, roles: new[] { "SuperAdmin" }).GetAsync("/api/groups/ownerless");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var groups = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(groups.EnumerateArray(), g => g.GetProperty("id").GetGuid() == ownerlessGroup.Id);
    }
}
