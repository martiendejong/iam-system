using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 1496: a scoped service account (client_credentials token, no role claims) may call
/// the two provisioning endpoints TaskManager's "Add Team Member" flow needs, gated on
/// exact permissions instead of roles:
///   - POST /api/users        requires the "users:create" permission
///   - POST /api/invitations  requires the "invitations:send" permission and an
///     onBehalfOfEmail naming the acting human (Invitation.InvitedByUserId is a hard FK
///     to Users; a service account is not a user row).
/// A service account WITHOUT the permission stays locked out, exactly like before.
/// </summary>
public class ServiceAccountProvisioningTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    private const string RootTenantId = "11111111-1111-1111-1111-111111111111";
    private const string UserRoleId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"; // "User" role from seed
    private const string SeededAdminEmail = "admin@test.com";

    public ServiceAccountProvisioningTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient ServiceClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(
            TestAuthenticationHelper.GenerateServiceAccountToken("taskmanager", permissions));
        return client;
    }

    // ── POST /api/users ──

    [Fact]
    public async Task CreateUser_ServiceAccountWithUsersCreatePermission_Returns201()
    {
        var client = ServiceClient("users:create");

        var response = await client.PostAsJsonAsync("/api/users", new
        {
            email = "sa.created@test.com",
            password = "Str0ngTestPassw0rd!",
            firstName = "Service",
            lastName = "Created"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sa.created@test.com", body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task CreateUser_ServiceAccountWithoutPermission_Returns403()
    {
        // A permission list without users:create — e.g. only the invite permission.
        var client = ServiceClient("invitations:send");

        var response = await client.PostAsJsonAsync("/api/users", new
        {
            email = "sa.forbidden@test.com",
            password = "Str0ngTestPassw0rd!"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_ServiceAccount_DuplicateEmail_Returns409()
    {
        var client = ServiceClient("users:create");

        var response = await client.PostAsJsonAsync("/api/users", new
        {
            email = SeededAdminEmail, // seeded user
            password = "Str0ngTestPassw0rd!"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already exists", body.GetProperty("error").GetString());
    }

    // ── POST /api/invitations ──

    [Fact]
    public async Task SendInvitation_ServiceAccountWithPermissionAndOnBehalfOf_Returns200()
    {
        var client = ServiceClient("invitations:send");

        var response = await client.PostAsJsonAsync("/api/invitations", new
        {
            email = "sa.invitee@test.com",
            tenantId = RootTenantId,
            roleId = UserRoleId,
            onBehalfOfEmail = SeededAdminEmail
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sa.invitee@test.com", body.GetProperty("email").GetString());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SendInvitation_ServiceAccountWithoutOnBehalfOf_Returns400()
    {
        var client = ServiceClient("invitations:send");

        var response = await client.PostAsJsonAsync("/api/invitations", new
        {
            email = "sa.no-actor@test.com",
            tenantId = RootTenantId,
            roleId = UserRoleId
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("onBehalfOfEmail", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SendInvitation_ServiceAccountWithUnknownOnBehalfOf_Returns400()
    {
        var client = ServiceClient("invitations:send");

        var response = await client.PostAsJsonAsync("/api/invitations", new
        {
            email = "sa.unknown-actor@test.com",
            tenantId = RootTenantId,
            roleId = UserRoleId,
            onBehalfOfEmail = "nobody-with-this-email@test.com"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendInvitation_ServiceAccountWithoutPermission_Returns403()
    {
        var client = ServiceClient("users:create"); // wrong permission for this endpoint

        var response = await client.PostAsJsonAsync("/api/invitations", new
        {
            email = "sa.locked-out@test.com",
            tenantId = RootTenantId,
            roleId = UserRoleId,
            onBehalfOfEmail = SeededAdminEmail
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Service-account MANAGEMENT must be admin-gated (SuperAdmin/SystemAdmin) ──
    // Without this, any authenticated user could mint their own service account with
    // "users:create"/"invitations:send" (or rotate the secret of an existing scoped one)
    // and walk straight through the permission gates above — closing that hole is part
    // of admitting service accounts to provisioning endpoints at all. The gate mirrors
    // OAuthClientsController's [Authorize(Roles = "SuperAdmin,SystemAdmin")] convention.

    private HttpClient OrdinaryUserClient()
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.NewGuid(), "ordinary@test.com", new[] { "User" }));
        return client;
    }

    [Fact]
    public async Task CreateServiceAccount_OrdinaryUser_Returns403()
    {
        var client = OrdinaryUserClient();

        var response = await client.PostAsJsonAsync("/api/service-accounts", new
        {
            name = "escalation-attempt",
            permissions = new[] { "users:create", "invitations:send" }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateServiceAccount_OrdinaryUser_Returns403()
    {
        var client = OrdinaryUserClient();

        var response = await client.PutAsJsonAsync($"/api/service-accounts/{Guid.NewGuid()}", new
        {
            permissions = new[] { "users:create", "invitations:send" }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteServiceAccount_OrdinaryUser_Returns403()
    {
        var client = OrdinaryUserClient();

        var response = await client.DeleteAsync($"/api/service-accounts/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RotateServiceAccountSecret_OrdinaryUser_Returns403()
    {
        var client = OrdinaryUserClient();

        var response = await client.PostAsync($"/api/service-accounts/{Guid.NewGuid()}/rotate-secret", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateServiceAccount_ServiceAccountToken_Returns403()
    {
        // A service account must not be able to mint further service accounts
        // (chained escalation): its token carries permissions but no role claims.
        var client = ServiceClient("users:create", "invitations:send");

        var response = await client.PostAsJsonAsync("/api/service-accounts", new
        {
            name = "chained-escalation-attempt",
            permissions = new[] { "users:create" }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateServiceAccount_SuperAdmin_StillWorks()
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), SeededAdminEmail, new[] { "SuperAdmin" }));

        var response = await client.PostAsJsonAsync("/api/service-accounts", new
        {
            name = "taskmanager-provisioning",
            permissions = new[] { "users:create", "invitations:send" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("svc_", body.GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task SendInvitation_HumanAdminRole_StillWorksWithoutOnBehalfOf()
    {
        // Regression guard: the pre-existing role-based path is untouched.
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), SeededAdminEmail, new[] { "SuperAdmin" }));

        var response = await client.PostAsJsonAsync("/api/invitations", new
        {
            email = "human.invitee@test.com",
            tenantId = RootTenantId,
            roleId = UserRoleId
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}
