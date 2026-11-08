using System.Net;
using System.Net.Http.Headers;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5631: GET /api/access-requests/{id} is limited to the requester, an assigned approver (direct or through the
/// approver role) and an admin of the request's tenant; everyone else gets the same 404 as for an unknown id.
/// </summary>
public class AccessRequestReadAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Domain = "@ar-read.test";

    private readonly IAMTestWebApplicationFactory _factory;

    public AccessRequestReadAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private async Task<Guid> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}{Domain}", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> RoleIdAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == name);
        if (role == null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = name, Description = name, TenantId = RootTenantId };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }
        return role.Id;
    }

    private async Task GrantRoleAsync(Guid userId, Guid roleId, Guid? tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId, TenantId = tenantId, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private HttpClient ClientFor(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}{Domain}", roles, null));
        return client;
    }

    private async Task<Guid> SeedRequestAsync(Guid requesterId, Guid tenantId, Guid? approverId, Guid? approverRoleId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var request = new AccessRequest { RequesterId = requesterId, TenantId = tenantId, ResourceType = "Role", Justification = "secret justification" };
        request.ApprovalSteps.Add(new ApprovalStep { AccessRequestId = request.Id, StepOrder = 1, ApproverId = approverId, ApproverRoleId = approverRoleId });
        db.AccessRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/access-requests/{id}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Requester_CanRead()
    {
        var tenant = await CreateTenantAsync();
        var requester = await CreateUserAsync();
        var id = await SeedRequestAsync(requester, tenant, null, null);

        var (status, body) = await GetAsync(ClientFor(requester), id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("secret justification", body);
    }

    [Fact]
    public async Task DirectApprover_CanRead()
    {
        var tenant = await CreateTenantAsync();
        var approver = await CreateUserAsync();
        var id = await SeedRequestAsync(await CreateUserAsync(), tenant, approver, null);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(ClientFor(approver), id)).Status);
    }

    [Fact]
    public async Task RoleBasedApprover_CanRead()
    {
        var tenant = await CreateTenantAsync();
        var roleId = await RoleIdAsync($"ar-approvers-{Guid.NewGuid():N}");
        var approver = await CreateUserAsync();
        await GrantRoleAsync(approver, roleId, null);
        var id = await SeedRequestAsync(await CreateUserAsync(), tenant, null, roleId);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(ClientFor(approver), id)).Status);
    }

    [Fact]
    public async Task TenantAdmin_CanRead_ButAdminOfOtherTenantCannot()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var adminRole = await RoleIdAsync("TenantAdmin");
        var admin = await CreateUserAsync();
        var otherAdmin = await CreateUserAsync();
        await GrantRoleAsync(admin, adminRole, tenant);
        await GrantRoleAsync(otherAdmin, adminRole, otherTenant);
        var id = await SeedRequestAsync(await CreateUserAsync(), tenant, null, null);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(ClientFor(admin, "TenantAdmin"), id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(ClientFor(otherAdmin, "TenantAdmin"), id)).Status);
    }

    [Fact]
    public async Task UnrelatedUser_GetsSame404AsUnknownId()
    {
        var tenant = await CreateTenantAsync();
        var id = await SeedRequestAsync(await CreateUserAsync(), tenant, await CreateUserAsync(), null);
        var stranger = ClientFor(await CreateUserAsync());

        var denied = await GetAsync(stranger, id);
        var unknown = await GetAsync(stranger, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, denied.Status);
        Assert.Equal(unknown.Status, denied.Status);
        Assert.Equal(unknown.Body, denied.Body);
        Assert.DoesNotContain("secret justification", denied.Body);
    }
}
