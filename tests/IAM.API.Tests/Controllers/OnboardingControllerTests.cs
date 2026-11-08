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

    /// <summary>Creates a fresh persisted user and an authenticated client for it (one org per user).</summary>
    private async Task<(HttpClient Client, Guid UserId)> NewUserClientAsync()
    {
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            db.Users.Add(new IAM.Core.Entities.User { Id = id, Email = $"{id:N}@onboarding.test", PasswordHash = "x" });
            await db.SaveChangesAsync();
        }
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(id, $"{id:N}@onboarding.test", new[] { "User" }));
        return (client, id);
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
        var (_client, userId) = await NewUserClientAsync();
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
        var (_client, userId) = await NewUserClientAsync();
        var response = await _client.PostAsJsonAsync("/api/onboarding/organization", new
        {
            name = "Acme Escalation Test Org"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var orgOwnerClient = _factory.CreateClient();
        orgOwnerClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            userId, "user@test.com", new[] { "OrganizationOwner" }));

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
        var (_client, _) = await NewUserClientAsync();
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

    [Fact]
    public async Task CreateOrganization_SecondCall_ReturnsConflictAndCreatesNoTenant()
    {
        var (client, userId) = await NewUserClientAsync();
        var first = await client.PostAsJsonAsync("/api/onboarding/organization", new { name = "First Org" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/onboarding/organization", new { name = "Second Org" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.Equal(1, await db.UserRoles.CountAsync(ur => ur.UserId == userId && ur.Tenant!.Type == "Organization"));
        Assert.False(await db.Tenants.AnyAsync(t => t.Name == "Second Org"));
    }

    [Fact]
    public async Task CreateOrganization_ParallelCalls_CreateOnlyOneOrganization()
    {
        var (client, userId) = await NewUserClientAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            client.PostAsJsonAsync("/api/onboarding/organization", new { name = $"Parallel Org {i}" })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.Equal(1, await db.UserRoles.CountAsync(ur => ur.UserId == userId && ur.Tenant!.Type == "Organization"));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("Line one\nLine two")]
    [InlineData("Tab\there")]
    [InlineData("<b>Bold</b> Org")]
    [InlineData("Evil\r\nBcc: x@y.z")]
    public async Task CreateOrganization_InvalidName_ReturnsBadRequest(string name)
    {
        var (client, _) = await NewUserClientAsync();
        var response = await client.PostAsJsonAsync("/api/onboarding/organization", new { name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrganization_NameOverMaxLength_ReturnsBadRequest_AndExactMaxIsAccepted()
    {
        var (client, _) = await NewUserClientAsync();
        var tooLong = await client.PostAsJsonAsync("/api/onboarding/organization", new { name = new string('a', 101) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var exact = await client.PostAsJsonAsync("/api/onboarding/organization", new { name = new string('a', 100) });
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
    }

    [Fact]
    public async Task CreateOrganization_RecipientAlreadyInvitedRecently_SkipsEmailButCreatesOrg()
    {
        var recipient = $"{Guid.NewGuid():N}@recipient-limit.example";
        var (first, _) = await NewUserClientAsync();
        var r1 = await first.PostAsJsonAsync("/api/onboarding/organization", new { name = "Recipient Org A", inviteEmail = recipient });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var (second, _) = await NewUserClientAsync();
        var r2 = await second.PostAsJsonAsync("/api/onboarding/organization", new { name = "Recipient Org B", inviteEmail = recipient.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        var body = await r2.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = body.GetProperty("organizationId").GetGuid();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("invitation").GetProperty("error").GetString()));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.False(await db.Invitations.AnyAsync(i => i.TenantId == orgId));
    }

    [Fact]
    public async Task CreateOrganization_PerUserInviteLimit_SkipsEmailButCreatesOrg()
    {
        var (client, userId) = await NewUserClientAsync();

        // Pre-seed recent invitations by this user up to the default limit (5 per hour).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var tenant = new IAM.Core.Entities.Tenant { Name = "Limit Seed", Type = "Organization", IsActive = true };
            var role = new IAM.Core.Entities.Role { Name = $"seed-{Guid.NewGuid():N}", TenantId = null };
            db.Tenants.Add(tenant);
            db.Roles.Add(role);
            for (var i = 0; i < 5; i++)
            {
                db.Invitations.Add(new IAM.Core.Entities.Invitation
                {
                    Email = $"seed{i}-{Guid.NewGuid():N}@seed.example",
                    TenantId = tenant.Id,
                    RoleId = role.Id,
                    Token = Guid.NewGuid().ToString("N"),
                    InvitedByUserId = userId,
                    ExpiresAt = DateTime.UtcNow.AddDays(7)
                });
            }
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/onboarding/organization", new
        {
            name = "Limited Org",
            inviteEmail = $"{Guid.NewGuid():N}@per-user-limit.example"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("invitation").GetProperty("error").GetString()));
    }
}
