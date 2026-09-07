using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for the self-service Organization onboarding step (task 1741): a
/// freshly registered user has no roles at all, so TenantsController.CreateTenant and
/// InvitationsController.SendInvitation (both gated to SuperAdmin/BuildingOwner) are
/// unreachable until OnboardingController grants them one on their own new tenant.
/// </summary>
public class OnboardingControllerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OnboardingControllerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("88888888-8888-8888-8888-888888888888"), "user@test.com", new[] { "User" }));
    }

    [Fact]
    public async Task GetStatus_NewUserWithNoOrganization_ReturnsNeedsSetupTrue()
    {
        // The two seeded test users (admin@test.com / user@test.com) both already belong
        // to "Root Tenant", which defaults to Type "Organization" (Tenant.Type's own
        // default) - so this needs its own never-seeded identity to actually exercise the
        // "brand new user" case GetStatus exists for. GetStatus only reads UserRoles for
        // the claimed id, so the user need not exist in the Users table for this check.
        using var freshClient = _factory.CreateClient();
        freshClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.NewGuid(), "brand.new.user@test.com", new[] { "User" }));

        var response = await freshClient.GetAsync("/api/onboarding/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("needsOrganizationSetup").GetBoolean());
    }

    [Fact]
    public async Task CreateOrganization_GrantsCallerAScopedOwnerRole_AndFlipsStatus()
    {
        var response = await _client.PostAsJsonAsync("/api/onboarding/organization", new
        {
            name = "Acme Test Org"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = body.GetProperty("organizationId").GetGuid();
        Assert.Equal("OrganizationOwner", body.GetProperty("role").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == orgId);
        Assert.NotNull(tenant);
        Assert.Equal("Organization", tenant!.Type);

        var userId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var ownerAssignment = await db.UserRoles
            .Include(ur => ur.Role)
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.TenantId == orgId);
        Assert.NotNull(ownerAssignment);
        Assert.Equal("OrganizationOwner", ownerAssignment!.Role.Name);

        var statusResponse = await _client.GetAsync("/api/onboarding/status");
        var statusBody = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(statusBody.GetProperty("needsOrganizationSetup").GetBoolean());
    }

    [Fact]
    public async Task CreateOrganization_GrantedRole_CannotCreateOrManageOtherTenants()
    {
        // Regression test for the privilege-escalation review finding on this task (1741):
        // OnboardingController used to auto-grant the seeded "BuildingOwner" role, which
        // every [Authorize(Roles = "...BuildingOwner...")] gate in the app recognizes with
        // no tenant-ownership check in the method body - letting any self-registered
        // customer manage every tenant on the platform, not just their own. The onboarding
        // role must stay unrecognized by those gates.
        var response = await _client.PostAsJsonAsync("/api/onboarding/organization", new
        {
            name = "Acme Escalation Test Org"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var orgOwnerClient = _factory.CreateClient();
        orgOwnerClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("88888888-8888-8888-8888-888888888888"), "user@test.com", new[] { "OrganizationOwner" }));

        var createTenantResponse = await orgOwnerClient.PostAsJsonAsync("/api/tenants", new
        {
            name = "Someone Else's Tenant",
            type = "Building",
            parentTenantId = (Guid?)null,
            metadata = new Dictionary<string, object>(),
            settings = new Dictionary<string, object>()
        });
        Assert.Equal(HttpStatusCode.Forbidden, createTenantResponse.StatusCode);

        var sendInvitationResponse = await orgOwnerClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "victim@example.com",
            tenantId = Guid.NewGuid(),
            roleId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.Forbidden, sendInvitationResponse.StatusCode);
    }

    [Fact]
    public async Task CreateOrganization_WithInviteEmail_SendsInvitationReusingSharedService()
    {
        var response = await _client.PostAsJsonAsync("/api/onboarding/organization", new
        {
            name = "Acme Test Org With Invite",
            inviteEmail = "teammate@acme-test.example"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = body.GetProperty("organizationId").GetGuid();
        var invitation = body.GetProperty("invitation");
        Assert.Equal("teammate@acme-test.example", invitation.GetProperty("email").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var stored = await db.Invitations.FirstOrDefaultAsync(i => i.TenantId == orgId && i.Email == "teammate@acme-test.example");
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task CreateOrganization_MissingName_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/onboarding/organization", new
        {
            name = ""
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
