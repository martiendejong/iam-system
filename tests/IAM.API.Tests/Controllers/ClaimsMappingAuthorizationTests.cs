using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4715: the Claims Mapping API (/api/claims-mapping) is restricted to
/// SuperAdmin and SystemAdmin. Plain users get 403 on all 8 actions; anonymous
/// callers still get 401. Administrators see no change.
/// </summary>
public class ClaimsMappingAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    private const string PlainUserId = "88888888-8888-8888-8888-888888888888";
    private const string AdminUserId = "99999999-9999-9999-9999-999999999999";
    private const string AdminEmail = "admin@test.com";
    private const string TestClientId = "test-client";

    public ClaimsMappingAuthorizationTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient PlainUserClient()
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse(PlainUserId), "user@test.com", new[] { "User" }));
        return client;
    }

    private HttpClient SuperAdminClient()
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse(AdminUserId), AdminEmail, new[] { "SuperAdmin" }));
        return client;
    }

    private HttpClient SystemAdminClient()
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse(AdminUserId), AdminEmail, new[] { "SystemAdmin" }));
        return client;
    }

    private HttpClient AnonymousClient() => _factory.CreateClient();

    // ---- GET /api/claims-mapping/rules ----

    [Fact]
    public async Task GetRules_PlainUser_Returns403()
    {
        var response = await PlainUserClient()
            .GetAsync($"/api/claims-mapping/rules?clientId={TestClientId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRules_Anonymous_Returns401()
    {
        var response = await AnonymousClient()
            .GetAsync($"/api/claims-mapping/rules?clientId={TestClientId}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRules_SuperAdmin_Returns200()
    {
        var response = await SuperAdminClient()
            .GetAsync($"/api/claims-mapping/rules?clientId={TestClientId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetRules_SystemAdmin_Returns200()
    {
        var response = await SystemAdminClient()
            .GetAsync($"/api/claims-mapping/rules?clientId={TestClientId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- POST /api/claims-mapping/rules ----

    [Fact]
    public async Task CreateRule_PlainUser_Returns403()
    {
        var response = await PlainUserClient().PostAsJsonAsync("/api/claims-mapping/rules", new
        {
            clientId = TestClientId,
            sourceType = "UserAttribute",
            sourcePath = "email",
            targetClaim = "custom_email"
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateRule_Anonymous_Returns401()
    {
        var response = await AnonymousClient().PostAsJsonAsync("/api/claims-mapping/rules", new
        {
            clientId = TestClientId,
            sourceType = "UserAttribute",
            sourcePath = "email",
            targetClaim = "custom_email"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- GET /api/claims-mapping/rules/{id} ----

    [Fact]
    public async Task GetRule_PlainUser_Returns403()
    {
        var response = await PlainUserClient()
            .GetAsync($"/api/claims-mapping/rules/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRule_Anonymous_Returns401()
    {
        var response = await AnonymousClient()
            .GetAsync($"/api/claims-mapping/rules/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- PUT /api/claims-mapping/rules/{id} ----

    [Fact]
    public async Task UpdateRule_PlainUser_Returns403()
    {
        var response = await PlainUserClient().PutAsJsonAsync(
            $"/api/claims-mapping/rules/{Guid.NewGuid()}", new
            {
                sourceType = "UserAttribute",
                sourcePath = "email",
                targetClaim = "custom_email"
            });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRule_Anonymous_Returns401()
    {
        var response = await AnonymousClient().PutAsJsonAsync(
            $"/api/claims-mapping/rules/{Guid.NewGuid()}", new
            {
                sourceType = "UserAttribute",
                sourcePath = "email",
                targetClaim = "custom_email"
            });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- DELETE /api/claims-mapping/rules/{id} ----

    [Fact]
    public async Task DeleteRule_PlainUser_Returns403()
    {
        var response = await PlainUserClient()
            .DeleteAsync($"/api/claims-mapping/rules/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRule_Anonymous_Returns401()
    {
        var response = await AnonymousClient()
            .DeleteAsync($"/api/claims-mapping/rules/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- GET /api/claims-mapping/token-config ----

    [Fact]
    public async Task GetTokenConfig_PlainUser_Returns403()
    {
        var response = await PlainUserClient()
            .GetAsync($"/api/claims-mapping/token-config?clientId={TestClientId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTokenConfig_Anonymous_Returns401()
    {
        var response = await AnonymousClient()
            .GetAsync($"/api/claims-mapping/token-config?clientId={TestClientId}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTokenConfig_SuperAdmin_Returns200()
    {
        var response = await SuperAdminClient()
            .GetAsync($"/api/claims-mapping/token-config?clientId={TestClientId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- PUT /api/claims-mapping/token-config ----

    [Fact]
    public async Task UpsertTokenConfig_PlainUser_Returns403()
    {
        var response = await PlainUserClient().PutAsJsonAsync("/api/claims-mapping/token-config", new
        {
            clientId = TestClientId,
            accessTokenLifetimeMinutes = 60,
            refreshTokenLifetimeDays = 30
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpsertTokenConfig_Anonymous_Returns401()
    {
        var response = await AnonymousClient().PutAsJsonAsync("/api/claims-mapping/token-config", new
        {
            clientId = TestClientId,
            accessTokenLifetimeMinutes = 60,
            refreshTokenLifetimeDays = 30
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpsertTokenConfig_SuperAdmin_Returns200()
    {
        var response = await SuperAdminClient().PutAsJsonAsync("/api/claims-mapping/token-config", new
        {
            clientId = TestClientId,
            accessTokenLifetimeMinutes = 30,
            refreshTokenLifetimeDays = 14
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A rejected PUT (plain user = 403) must not modify the stored configuration.
    /// </summary>
    [Fact]
    public async Task UpsertTokenConfig_PlainUserRejection_DoesNotChangeStoredConfig()
    {
        var adminClient = SuperAdminClient();
        var userClient = PlainUserClient();

        // Set initial config as SuperAdmin.
        await adminClient.PutAsJsonAsync("/api/claims-mapping/token-config", new
        {
            clientId = "isolated-client",
            accessTokenLifetimeMinutes = 20,
            refreshTokenLifetimeDays = 10
        });

        // Plain user attempts to change it -- must be rejected.
        var rejected = await userClient.PutAsJsonAsync("/api/claims-mapping/token-config", new
        {
            clientId = "isolated-client",
            accessTokenLifetimeMinutes = 1440,
            refreshTokenLifetimeDays = 365
        });
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

        // The original config must be unchanged.
        var check = await adminClient
            .GetAsync("/api/claims-mapping/token-config?clientId=isolated-client");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var body = await check.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(20, body.GetProperty("accessTokenLifetimeMinutes").GetInt32());
        Assert.Equal(10, body.GetProperty("refreshTokenLifetimeDays").GetInt32());
    }

    // ---- GET /api/claims-mapping/preview ----

    [Fact]
    public async Task PreviewToken_PlainUser_Returns403()
    {
        var response = await PlainUserClient()
            .GetAsync($"/api/claims-mapping/preview?clientId={TestClientId}&userId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PreviewToken_Anonymous_Returns401()
    {
        var response = await AnonymousClient()
            .GetAsync($"/api/claims-mapping/preview?clientId={TestClientId}&userId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
