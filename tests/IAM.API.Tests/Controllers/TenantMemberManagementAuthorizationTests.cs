using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4700: member list / role change / removal (TenantsController) and invitation send / bulk / list / pending /
/// revoke / accept (InvitationsController) are limited to SuperAdmin and to an active BuildingOwner/BuildingManager
/// UserRole in the request's own tenant, and share one role-grant rule: platform-wide roles are SuperAdmin-only,
/// owner-level roles need an owner, a caller cannot change or remove themselves, the last owner is protected.
/// </summary>
public class TenantMemberManagementAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public TenantMemberManagementAuthorizationTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ----- helpers ---------------------------------------------------------------------------

    private IAMDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAMDbContext>();

    private async Task<Tenant> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        Db(scope).Tenants.Add(tenant);
        await Db(scope).SaveChangesAsync();
        return tenant;
    }

    private async Task<User> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@tenant-members.test", PasswordHash = "not-used", IsActive = true };
        Db(scope).Users.Add(user);
        await Db(scope).SaveChangesAsync();
        return user;
    }

    private async Task<Role> CreateRoleAsync(string name, Guid? tenantId = null, string permissions = "[\"Room.View\"]")
    {
        using var scope = _factory.Services.CreateScope();
        var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Permissions = permissions };
        Db(scope).Roles.Add(role);
        await Db(scope).SaveChangesAsync();
        return role;
    }

    private async Task GrantAsync(Guid userId, Role role, Guid? tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        Db(scope).UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenantId,
            ExpiresAt = expiresAt, GrantedAt = DateTime.UtcNow
        });
        await Db(scope).SaveChangesAsync();
    }

    /// <summary>A user holding <paramref name="role"/> in the tenant, with a client whose token carries the role claim.</summary>
    private async Task<(User User, HttpClient Client)> ManagerAsync(Role role, Tenant tenant, DateTime? expiresAt = null)
    {
        var user = await CreateUserAsync();
        await GrantAsync(user.Id, role, tenant.Id, expiresAt);
        return (user, ClientAs(user.Id, role.Name));
    }

    private HttpClient ClientAs(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@tenant-members.test", roles, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), "SuperAdmin");

    private async Task<List<string>> RoleNamesAsync(Guid userId, Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && ur.TenantId == tenantId)
            .Select(ur => ur.Role.Name).ToListAsync();
    }

    private static string RoleUrl(Guid tenantId, Guid userId) => $"/api/tenants/{tenantId}/members/{userId}/role";
    private static string MemberUrl(Guid tenantId, Guid userId) => $"/api/tenants/{tenantId}/members/{userId}";

    private static object RoleBody(Guid roleId) => new { roleId };

    private async Task<Invitation> SeedInvitationAsync(Guid tenantId, Role role, Guid invitedBy, string status = "Pending")
    {
        using var scope = _factory.Services.CreateScope();
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@invited.test", TenantId = tenantId, RoleId = role.Id,
            Token = Guid.NewGuid().ToString("N"), Status = status, InvitedByUserId = invitedBy,
            ExpiresAt = DateTime.UtcNow.AddDays(3)
        };
        Db(scope).Set<Invitation>().Add(invitation);
        await Db(scope).SaveChangesAsync();
        return invitation;
    }

    private async Task<Invitation?> GetInvitationAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).Set<Invitation>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
    }

    private async Task<int> InvitationCountAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).Set<Invitation>().CountAsync(i => i.TenantId == tenantId);
    }

    /// <summary>A tenant with an owner, a manager and a plain member, plus their clients.</summary>
    private sealed record Scene(Tenant Tenant, Role Owner, Role Manager, Role Resident,
        User OwnerUser, HttpClient OwnerClient, User ManagerUser, HttpClient ManagerClient, User Member);

    private async Task<Scene> CreateSceneAsync()
    {
        var tenant = await CreateTenantAsync();
        var owner = await CreateRoleAsync("BuildingOwner");
        var manager = await CreateRoleAsync("BuildingManager");
        var resident = await CreateRoleAsync("Resident");
        var (ownerUser, ownerClient) = await ManagerAsync(owner, tenant);
        var (managerUser, managerClient) = await ManagerAsync(manager, tenant);
        var member = await CreateUserAsync();
        await GrantAsync(member.Id, resident, tenant.Id);
        return new Scene(tenant, owner, manager, resident, ownerUser, ownerClient, managerUser, managerClient, member);
    }

    // ----- members list -----------------------------------------------------------------------

    [Fact]
    public async Task GetMembers_PlainUser_IsForbidden_EvenForAnUnknownTenant()
    {
        var scene = await CreateSceneAsync();
        var plain = ClientAs(Guid.NewGuid(), "User");

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync($"/api/tenants/{scene.Tenant.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync($"/api/tenants/{Guid.NewGuid()}/members")).StatusCode);
    }

    [Fact]
    public async Task GetMembers_ManagerOfAnotherTenant_IsForbidden_UnknownTenantToo()
    {
        var a = await CreateSceneAsync();
        var b = await CreateSceneAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await a.ManagerClient.GetAsync($"/api/tenants/{b.Tenant.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.ManagerClient.GetAsync($"/api/tenants/{Guid.NewGuid()}/members")).StatusCode);
    }

    [Fact]
    public async Task GetMembers_RoleClaimOnly_UnscopedOrExpiredRow_IsForbidden()
    {
        var scene = await CreateSceneAsync();
        var claimOnly = ClientAs((await CreateUserAsync()).Id, "BuildingManager");
        var unscoped = await CreateUserAsync();
        await GrantAsync(unscoped.Id, scene.Manager, null);
        var (_, expired) = await ManagerAsync(scene.Manager, scene.Tenant, DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(HttpStatusCode.Forbidden, (await claimOnly.GetAsync($"/api/tenants/{scene.Tenant.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(unscoped.Id, "BuildingManager").GetAsync($"/api/tenants/{scene.Tenant.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await expired.GetAsync($"/api/tenants/{scene.Tenant.Id}/members")).StatusCode);
    }

    [Fact]
    public async Task GetMembers_OwnManagerAndOwner_ListTheirMembers()
    {
        var scene = await CreateSceneAsync();

        foreach (var client in new[] { scene.ManagerClient, scene.OwnerClient })
        {
            var response = await client.GetAsync($"/api/tenants/{scene.Tenant.Id}/members");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var ids = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
                .Select(m => m.GetProperty("userId").GetGuid()).ToList();
            Assert.Contains(scene.Member.Id, ids);
        }
    }

    [Fact]
    public async Task GetMembers_SuperAdmin_AnyTenant_AndUnknownTenantIsNotFound()
    {
        var scene = await CreateSceneAsync();

        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().GetAsync($"/api/tenants/{scene.Tenant.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SuperAdmin().GetAsync($"/api/tenants/{Guid.NewGuid()}/members")).StatusCode);
    }

    // ----- role change: tenant scope ----------------------------------------------------------

    [Fact]
    public async Task ChangeRole_ManagerOfTenantA_OnTenantB_IsForbidden_AndChangesNothing()
    {
        var a = await CreateSceneAsync();
        var b = await CreateSceneAsync();

        var response = await a.ManagerClient.PutAsJsonAsync(RoleUrl(b.Tenant.Id, b.Member.Id), RoleBody(b.Manager.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(new[] { "Resident" }, await RoleNamesAsync(b.Member.Id, b.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_PlainUser_IsForbidden_BeforeAnyLookup()
    {
        var scene = await CreateSceneAsync();
        var plain = ClientAs(Guid.NewGuid(), "User");

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(scene.Manager.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsJsonAsync(RoleUrl(Guid.NewGuid(), Guid.NewGuid()), RoleBody(Guid.NewGuid()))).StatusCode);
    }

    // ----- role change: what may be granted ---------------------------------------------------

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    [InlineData("SecurityAdmin")]
    [InlineData("ComplianceOfficer")]
    [InlineData("EmergencyAccess")]
    [InlineData("Admin")]
    [InlineData("systemadmin")]
    public async Task ChangeRole_ManagerAndOwner_CannotGrantPlatformRoles_AndTheRowIsUnchanged(string roleName)
    {
        var scene = await CreateSceneAsync();
        var platform = await CreateRoleAsync(roleName);

        var asManager = await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(platform.Id));
        var asOwner = await scene.OwnerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(platform.Id));

        Assert.Equal(HttpStatusCode.Forbidden, asManager.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asOwner.StatusCode);
        Assert.Equal(new[] { "Resident" }, await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_ACustomRoleOfTheTenantNamedSystemAdmin_IsRefused()
    {
        var scene = await CreateSceneAsync();
        var fake = await CreateRoleAsync("SystemAdmin", tenantId: scene.Tenant.Id);

        var response = await scene.OwnerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(fake.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(new[] { "Resident" }, await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    [Theory]
    [InlineData("BuildingOwner")]
    [InlineData("TenantAdmin")]
    [InlineData("OrganizationOwner")]
    public async Task ChangeRole_ManagerCannotGrantOwnerLevelRoles(string roleName)
    {
        var scene = await CreateSceneAsync();
        var ownerLevel = await CreateRoleAsync(roleName);

        var response = await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(ownerLevel.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(new[] { "Resident" }, await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_OwnerCanGrantBuildingOwner()
    {
        var scene = await CreateSceneAsync();

        var response = await scene.OwnerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(scene.Owner.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "BuildingOwner" }, await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_ManagerCanGrantOrdinaryAndOwnTenantCustomRoles()
    {
        var scene = await CreateSceneAsync();
        var contractor = await CreateRoleAsync("Contractor");
        var custom = await CreateRoleAsync("Front Desk", tenantId: scene.Tenant.Id);

        Assert.Equal(HttpStatusCode.OK, (await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(scene.Manager.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(contractor.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(custom.Id))).StatusCode);
        Assert.Equal(new[] { "Front Desk" }, await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_ACustomRoleOfAnotherTenant_IsRefused()
    {
        var scene = await CreateSceneAsync();
        var other = await CreateTenantAsync();
        var foreign = await CreateRoleAsync("Front Desk", tenantId: other.Id);

        var response = await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(foreign.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChangeRole_UnknownRole_IsNotFound_ForAManager()
    {
        var scene = await CreateSceneAsync();

        var response = await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ChangeRole_SuperAdminCanGrantAnything()
    {
        var scene = await CreateSceneAsync();
        var systemAdmin = await CreateRoleAsync("SystemAdmin");

        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(systemAdmin.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(scene.Owner.Id))).StatusCode);
        Assert.Equal(new[] { "BuildingOwner" }, await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    // ----- role change: self, owners, last owner ----------------------------------------------

    [Fact]
    public async Task ChangeRole_CallerCannotChangeTheirOwnRole()
    {
        var scene = await CreateSceneAsync();
        var contractor = await CreateRoleAsync("Contractor");

        var asManager = await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.ManagerUser.Id), RoleBody(contractor.Id));
        var asOwner = await scene.OwnerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.OwnerUser.Id), RoleBody(contractor.Id));

        Assert.Equal(HttpStatusCode.Forbidden, asManager.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asOwner.StatusCode);
        Assert.Equal(new[] { "BuildingManager" }, await RoleNamesAsync(scene.ManagerUser.Id, scene.Tenant.Id));
        Assert.Equal(new[] { "BuildingOwner" }, await RoleNamesAsync(scene.OwnerUser.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_ManagerCannotTouchAnOwner_ButAnOwnerCanDemoteAnotherOwner()
    {
        var scene = await CreateSceneAsync();
        var (secondOwner, _) = await ManagerAsync(scene.Owner, scene.Tenant);

        var asManager = await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, secondOwner.Id), RoleBody(scene.Resident.Id));
        var asOwner = await scene.OwnerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, secondOwner.Id), RoleBody(scene.Resident.Id));

        Assert.Equal(HttpStatusCode.Forbidden, asManager.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
        Assert.Equal(new[] { "Resident" }, await RoleNamesAsync(secondOwner.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_TheLastBuildingOwnerCannotBeDemoted_EvenBySuperAdmin()
    {
        var scene = await CreateSceneAsync();

        var response = await SuperAdmin().PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.OwnerUser.Id), RoleBody(scene.Resident.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(new[] { "BuildingOwner" }, await RoleNamesAsync(scene.OwnerUser.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_TheLastOwnerKeepingTheOwnerRoleIsFine()
    {
        var scene = await CreateSceneAsync();

        var response = await SuperAdmin().PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.OwnerUser.Id), RoleBody(scene.Owner.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----- member removal ---------------------------------------------------------------------

    [Fact]
    public async Task RemoveMember_PlainUser_AndManagerOfAnotherTenant_AreForbidden()
    {
        var a = await CreateSceneAsync();
        var b = await CreateSceneAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(Guid.NewGuid(), "User").DeleteAsync(MemberUrl(a.Tenant.Id, a.Member.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.ManagerClient.DeleteAsync(MemberUrl(a.Tenant.Id, a.Member.Id))).StatusCode);
        Assert.Equal(new[] { "Resident" }, await RoleNamesAsync(a.Member.Id, a.Tenant.Id));
    }

    [Fact]
    public async Task RemoveMember_OwnManagerRemovesAMember()
    {
        var scene = await CreateSceneAsync();

        var response = await scene.ManagerClient.DeleteAsync(MemberUrl(scene.Tenant.Id, scene.Member.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task RemoveMember_CallerCannotRemoveThemselves()
    {
        var scene = await CreateSceneAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await scene.ManagerClient.DeleteAsync(MemberUrl(scene.Tenant.Id, scene.ManagerUser.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scene.OwnerClient.DeleteAsync(MemberUrl(scene.Tenant.Id, scene.OwnerUser.Id))).StatusCode);
        Assert.Equal(new[] { "BuildingManager" }, await RoleNamesAsync(scene.ManagerUser.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task RemoveMember_ManagerCannotRemoveAnOwner_TheLastOwnerCannotBeRemovedByAnyone()
    {
        var scene = await CreateSceneAsync();

        var asManager = await scene.ManagerClient.DeleteAsync(MemberUrl(scene.Tenant.Id, scene.OwnerUser.Id));
        var asSuperAdmin = await SuperAdmin().DeleteAsync(MemberUrl(scene.Tenant.Id, scene.OwnerUser.Id));

        Assert.Equal(HttpStatusCode.Forbidden, asManager.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, asSuperAdmin.StatusCode);
        Assert.Equal(new[] { "BuildingOwner" }, await RoleNamesAsync(scene.OwnerUser.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task RemoveMember_OwnerRemovesAManager_AndSuperAdminRemovesAnOwnerWhenAnotherRemains()
    {
        var scene = await CreateSceneAsync();
        var (secondOwner, _) = await ManagerAsync(scene.Owner, scene.Tenant);

        Assert.Equal(HttpStatusCode.OK, (await scene.OwnerClient.DeleteAsync(MemberUrl(scene.Tenant.Id, scene.ManagerUser.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().DeleteAsync(MemberUrl(scene.Tenant.Id, secondOwner.Id))).StatusCode);
    }

    // ----- invitations: send ------------------------------------------------------------------

    private static object InviteBody(Guid tenantId, Guid roleId, string? email = null) =>
        new { email = email ?? $"{Guid.NewGuid():N}@invitee.test", tenantId, roleId };

    [Fact]
    public async Task SendInvitation_PlainUser_RoleClaimOnly_AndManagerOfAnotherTenant_AreForbidden()
    {
        var a = await CreateSceneAsync();
        var b = await CreateSceneAsync();
        var claimOnly = ClientAs((await CreateUserAsync()).Id, "BuildingManager");

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(Guid.NewGuid(), "User").PostAsJsonAsync("/api/invitations", InviteBody(a.Tenant.Id, a.Resident.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await claimOnly.PostAsJsonAsync("/api/invitations", InviteBody(a.Tenant.Id, a.Resident.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.ManagerClient.PostAsJsonAsync("/api/invitations", InviteBody(a.Tenant.Id, a.Resident.Id))).StatusCode);
        Assert.Equal(0, await InvitationCountAsync(a.Tenant.Id));
    }

    [Fact]
    public async Task SendInvitation_ManagerCannotInviteIntoPlatformOrOwnerLevelRoles()
    {
        var scene = await CreateSceneAsync();
        var systemAdmin = await CreateRoleAsync("SystemAdmin");
        var fake = await CreateRoleAsync("SystemAdmin", tenantId: scene.Tenant.Id);

        foreach (var role in new[] { systemAdmin, fake, scene.Owner })
        {
            var response = await scene.ManagerClient.PostAsJsonAsync("/api/invitations", InviteBody(scene.Tenant.Id, role.Id));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(0, await InvitationCountAsync(scene.Tenant.Id));
    }

    [Fact]
    public async Task SendInvitation_ManagerCanInviteIntoOrdinaryRoles_OwnerAlsoIntoBuildingOwner()
    {
        var scene = await CreateSceneAsync();

        Assert.Equal(HttpStatusCode.OK, (await scene.ManagerClient.PostAsJsonAsync("/api/invitations", InviteBody(scene.Tenant.Id, scene.Resident.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scene.ManagerClient.PostAsJsonAsync("/api/invitations", InviteBody(scene.Tenant.Id, scene.Manager.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scene.OwnerClient.PostAsJsonAsync("/api/invitations", InviteBody(scene.Tenant.Id, scene.Owner.Id))).StatusCode);
    }

    [Fact]
    public async Task SendInvitation_SuperAdminCanInviteIntoAnything()
    {
        var scene = await CreateSceneAsync();
        var systemAdmin = await CreateRoleAsync("SystemAdmin");

        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().PostAsJsonAsync("/api/invitations", InviteBody(scene.Tenant.Id, systemAdmin.Id))).StatusCode);
    }

    [Fact]
    public async Task SendInvitation_ServiceAccount_KeepsItsGate_ButCannotGrantPlatformRoles()
    {
        var scene = await CreateSceneAsync();
        var systemAdmin = await CreateRoleAsync("SystemAdmin");
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateServiceAccountToken("taskmanager", "invitations:send"));
        object Body(Guid roleId) => new
        {
            email = $"{Guid.NewGuid():N}@sa.test", tenantId = scene.Tenant.Id, roleId, onBehalfOfEmail = "admin@test.com"
        };

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/invitations", Body(scene.Resident.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/invitations", Body(systemAdmin.Id))).StatusCode);
    }

    // ----- invitations: list, pending, revoke -------------------------------------------------

    [Fact]
    public async Task ListAndPending_PlainUser_AndManagerOfAnotherTenant_AreForbidden_OwnManagerAllowed()
    {
        var a = await CreateSceneAsync();
        var b = await CreateSceneAsync();
        await SeedInvitationAsync(a.Tenant.Id, a.Resident, a.ManagerUser.Id);

        foreach (var path in new[] { "/api/invitations", "/api/invitations/pending" })
        {
            var url = $"{path}?tenantId={a.Tenant.Id}";
            Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(Guid.NewGuid(), "User").GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await b.ManagerClient.GetAsync(url)).StatusCode);
            var own = await a.ManagerClient.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Single((await own.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
            Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Revoke_ManagerOfAnotherTenant_IsForbidden_AndTheInvitationStaysPending()
    {
        var a = await CreateSceneAsync();
        var b = await CreateSceneAsync();
        var invitation = await SeedInvitationAsync(a.Tenant.Id, a.Resident, a.ManagerUser.Id);

        var response = await b.ManagerClient.DeleteAsync($"/api/invitations/{invitation.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Pending", (await GetInvitationAsync(invitation.Id))!.Status);
    }

    [Fact]
    public async Task Revoke_UnknownId_IsForbiddenForCallersWhoManageNoTenant_NotFoundForManagers()
    {
        var scene = await CreateSceneAsync();
        var unknown = $"/api/invitations/{Guid.NewGuid()}";

        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(Guid.NewGuid(), "User").DeleteAsync(unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scene.ManagerClient.DeleteAsync(unknown)).StatusCode);
    }

    [Fact]
    public async Task Revoke_OwnManagerAndSuperAdmin_CanRevoke()
    {
        var scene = await CreateSceneAsync();
        var one = await SeedInvitationAsync(scene.Tenant.Id, scene.Resident, scene.ManagerUser.Id);
        var two = await SeedInvitationAsync(scene.Tenant.Id, scene.Resident, scene.ManagerUser.Id);

        Assert.Equal(HttpStatusCode.OK, (await scene.ManagerClient.DeleteAsync($"/api/invitations/{one.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().DeleteAsync($"/api/invitations/{two.Id}")).StatusCode);
        Assert.Equal("Revoked", (await GetInvitationAsync(one.Id))!.Status);
    }

    // ----- invitations: bulk ------------------------------------------------------------------

    private static MultipartFormDataContent BulkForm(Guid tenantId, string csv)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "File", "people.csv");
        form.Add(new StringContent(tenantId.ToString()), "TenantId");
        return form;
    }

    [Fact]
    public async Task Bulk_ManagerOfAnotherTenant_AndPlainUser_AreForbidden()
    {
        var a = await CreateSceneAsync();
        var b = await CreateSceneAsync();
        const string csv = "name,email,role\nA,a@bulk.test,Resident\n";

        Assert.Equal(HttpStatusCode.Forbidden, (await b.ManagerClient.PostAsync("/api/invitations/bulk", BulkForm(a.Tenant.Id, csv))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientAs(Guid.NewGuid(), "User").PostAsync("/api/invitations/bulk", BulkForm(a.Tenant.Id, csv))).StatusCode);
        Assert.Equal(0, await InvitationCountAsync(a.Tenant.Id));
    }

    [Fact]
    public async Task Bulk_ManagerRowsNamingPlatformOrOwnerRoles_FailPerRow_OrdinaryRowsSucceed()
    {
        var scene = await CreateSceneAsync();
        await CreateRoleAsync("SystemAdmin");
        const string csv = "name,email,role\nA,a@bulk.test,SystemAdmin\nB,b@bulk.test,BuildingOwner\nC,c@bulk.test,Resident\n";

        var response = await scene.ManagerClient.PostAsync("/api/invitations/bulk", BulkForm(scene.Tenant.Id, csv));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(2, body.GetProperty("failed").GetInt32());
        Assert.Equal(1, await InvitationCountAsync(scene.Tenant.Id));
    }

    [Fact]
    public async Task Bulk_OwnerMayInviteBuildingOwner_ButNotPlatformRoles()
    {
        var scene = await CreateSceneAsync();
        await CreateRoleAsync("SystemAdmin");
        const string csv = "name,email,role\nA,a@bulk.test,SystemAdmin\nB,b@bulk.test,BuildingOwner\n";

        var response = await scene.OwnerClient.PostAsync("/api/invitations/bulk", BulkForm(scene.Tenant.Id, csv));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
    }

    // ----- invitations: acceptance cannot bypass the rule -------------------------------------

    private static object AcceptBody => new { password = "Str0ngTestPassw0rd!", firstName = "New", lastName = "Member" };

    [Fact]
    public async Task Accept_AnOldInvitationForAPlatformRole_FromANonSuperAdminSender_IsRefusedAndRevoked()
    {
        var scene = await CreateSceneAsync();
        var systemAdmin = await CreateRoleAsync("SystemAdmin");
        var invitation = await SeedInvitationAsync(scene.Tenant.Id, systemAdmin, scene.ManagerUser.Id);

        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{invitation.Token}/accept", AcceptBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Revoked", (await GetInvitationAsync(invitation.Id))!.Status);
        using var scope = _factory.Services.CreateScope();
        Assert.False(await Db(scope).Users.AnyAsync(u => u.Email == invitation.Email));
    }

    [Fact]
    public async Task Accept_AnInvitationForAPlatformRole_FromASuperAdminSender_Works()
    {
        var scene = await CreateSceneAsync();
        var superAdminRole = await CreateRoleAsync("SuperAdmin");
        var sender = await CreateUserAsync();
        await GrantAsync(sender.Id, superAdminRole, null);
        var systemAdmin = await CreateRoleAsync("SystemAdmin");
        var invitation = await SeedInvitationAsync(scene.Tenant.Id, systemAdmin, sender.Id);

        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{invitation.Token}/accept", AcceptBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Accept_AnOrdinaryInvitation_StillWorks()
    {
        var scene = await CreateSceneAsync();
        var invitation = await SeedInvitationAsync(scene.Tenant.Id, scene.Resident, scene.ManagerUser.Id);

        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{invitation.Token}/accept", AcceptBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----- task 5147: federated app roles and other privileged global roles are SuperAdmin-only -----

    private async Task<Role> CreateAppRoleAsync(string name, string? category, Guid? tenantId = null, string permissions = "[]")
    {
        using var scope = _factory.Services.CreateScope();
        var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Category = category, Permissions = permissions };
        Db(scope).Roles.Add(role);
        await Db(scope).SaveChangesAsync();
        return role;
    }

    /// <summary>Federated app roles as AppRolesController registers them (global, Category app:{client}) plus a lookalike custom role.</summary>
    private async Task<Role[]> AppAndPrivilegedRolesAsync(Guid tenantId) => new[]
    {
        await CreateAppRoleAsync($"taskmanager{Guid.NewGuid():N}:admin", "app:taskmanager"),
        await CreateAppRoleAsync($"app:vault{Guid.NewGuid():N}:approver", "app:vault"),
        await CreateAppRoleAsync($"fedha{Guid.NewGuid():N}:member", null),
        await CreateAppRoleAsync($"custom{Guid.NewGuid():N}:admin", null, tenantId),
        await CreateAppRoleAsync($"workspace-admin-{Guid.NewGuid():N}", null),
        await CreateAppRoleAsync($"operator-{Guid.NewGuid():N}", null, permissions: "[\"*\"]"),
    };

    [Fact]
    public async Task ChangeRole_ManagerAndOwner_CannotGrantFederatedAppOrPrivilegedRoles_AndTheRowIsUnchanged()
    {
        var scene = await CreateSceneAsync();

        foreach (var role in await AppAndPrivilegedRolesAsync(scene.Tenant.Id))
        {
            var asManager = await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(role.Id));
            var asOwner = await scene.OwnerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(role.Id));

            Assert.True(asManager.StatusCode == HttpStatusCode.Forbidden, $"manager {role.Name}: {(int)asManager.StatusCode}");
            Assert.True(asOwner.StatusCode == HttpStatusCode.Forbidden, $"owner {role.Name}: {(int)asOwner.StatusCode}");
        }
        Assert.Equal(new[] { "Resident" }, await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_SuperAdmin_CanGrantFederatedAppAndPrivilegedRoles()
    {
        var scene = await CreateSceneAsync();
        var role = await CreateAppRoleAsync($"taskmanager{Guid.NewGuid():N}:admin", "app:taskmanager");

        var response = await SuperAdmin().PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(role.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { role.Name }, await RoleNamesAsync(scene.Member.Id, scene.Tenant.Id));
    }

    [Fact]
    public async Task ChangeRole_ManagerPlainLanguageError_NamesTheRole()
    {
        var scene = await CreateSceneAsync();
        var role = await CreateAppRoleAsync($"taskmanager{Guid.NewGuid():N}:admin", "app:taskmanager");

        var response = await scene.ManagerClient.PutAsJsonAsync(RoleUrl(scene.Tenant.Id, scene.Member.Id), RoleBody(role.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains(role.Name, error);
        Assert.Contains("SuperAdmin", error);
    }

    [Fact]
    public async Task SendInvitation_ManagerAndOwner_CannotInviteIntoFederatedAppOrPrivilegedRoles_SuperAdminCan()
    {
        var scene = await CreateSceneAsync();
        var roles = await AppAndPrivilegedRolesAsync(scene.Tenant.Id);

        foreach (var role in roles)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await scene.ManagerClient.PostAsJsonAsync("/api/invitations", InviteBody(scene.Tenant.Id, role.Id))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await scene.OwnerClient.PostAsJsonAsync("/api/invitations", InviteBody(scene.Tenant.Id, role.Id))).StatusCode);
        }
        Assert.Equal(0, await InvitationCountAsync(scene.Tenant.Id));

        Assert.Equal(HttpStatusCode.OK, (await SuperAdmin().PostAsJsonAsync("/api/invitations", InviteBody(scene.Tenant.Id, roles[0].Id))).StatusCode);
    }

    [Fact]
    public async Task Bulk_ManagerRowsNamingAppRoles_FailPerRow_OrdinaryRowsSucceed()
    {
        var scene = await CreateSceneAsync();
        var app = await CreateAppRoleAsync($"taskmanager{Guid.NewGuid():N}:admin", "app:taskmanager");
        var csv = $"name,email,role\nA,a@bulk5147.test,{app.Name}\nB,b@bulk5147.test,Resident\n";

        var response = await scene.OwnerClient.PostAsync("/api/invitations/bulk", BulkForm(scene.Tenant.Id, csv));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Accept_AnOldInvitationForAnAppRole_FromANonSuperAdminSender_IsRefusedAndRevoked()
    {
        var scene = await CreateSceneAsync();
        var app = await CreateAppRoleAsync($"taskmanager{Guid.NewGuid():N}:admin", "app:taskmanager");
        var invitation = await SeedInvitationAsync(scene.Tenant.Id, app, scene.OwnerUser.Id);

        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{invitation.Token}/accept", AcceptBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Revoked", (await GetInvitationAsync(invitation.Id))!.Status);
        using var scope = _factory.Services.CreateScope();
        Assert.False(await Db(scope).Users.AnyAsync(u => u.Email == invitation.Email));
    }

    [Fact]
    public async Task Accept_AnInvitationForAnAppRole_FromASuperAdminSender_Works()
    {
        var scene = await CreateSceneAsync();
        var superAdminRole = await CreateRoleAsync("SuperAdmin");
        var sender = await CreateUserAsync();
        await GrantAsync(sender.Id, superAdminRole, null);
        var app = await CreateAppRoleAsync($"taskmanager{Guid.NewGuid():N}:admin", "app:taskmanager");
        var invitation = await SeedInvitationAsync(scene.Tenant.Id, app, sender.Id);

        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{invitation.Token}/accept", AcceptBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
