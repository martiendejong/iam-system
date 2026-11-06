using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4715: the Claims Mapping API is administrator-only (SuperAdmin or SystemAdmin).
/// Plain users get 403 on all 8 actions, anonymous callers 401, admins keep working.
/// </summary>
public class ClaimsMappingAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;
    private static readonly Guid AnyId = Guid.NewGuid();

    public ClaimsMappingAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private HttpClient ClientWith(params string[] roles)
    {
        var client = _factory.CreateClient();
        if (roles.Length > 0)
            client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(Guid.NewGuid(), "cm@test.com", roles));
        return client;
    }

    private static readonly object TokenConfigBody = new
    {
        clientId = "cm-test-client",
        accessTokenLifetimeMinutes = 1440,
        refreshTokenLifetimeDays = 365,
        includeRoles = true,
        includePermissions = false,
        includeGroups = false
    };

    private static Task<HttpResponseMessage> Call(HttpClient c, int action) => action switch
    {
        0 => c.GetAsync("/api/claims-mapping/rules?clientId=cm-test-client"),
        1 => c.PostAsJsonAsync("/api/claims-mapping/rules", new { clientId = "cm-test-client" }),
        2 => c.GetAsync($"/api/claims-mapping/rules/{AnyId}"),
        3 => c.PutAsJsonAsync($"/api/claims-mapping/rules/{AnyId}", new { }),
        4 => c.DeleteAsync($"/api/claims-mapping/rules/{AnyId}"),
        5 => c.GetAsync("/api/claims-mapping/token-config?clientId=cm-test-client"),
        6 => c.PutAsJsonAsync("/api/claims-mapping/token-config", TokenConfigBody),
        _ => c.GetAsync($"/api/claims-mapping/preview?clientId=cm-test-client&userId={AnyId}")
    };

    public static IEnumerable<object[]> Actions() => Enumerable.Range(0, 8).Select(i => new object[] { i });

    [Theory, MemberData(nameof(Actions))]
    public async Task PlainUser_Gets403(int action)
    {
        var r = await Call(ClientWith("User"), action);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Anonymous_Gets401(int action)
    {
        var r = await Call(ClientWith(), action);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task Admin_CanReadAndWriteTokenConfig(string role)
    {
        var client = ClientWith(role);
        var put = await client.PutAsJsonAsync("/api/claims-mapping/token-config", TokenConfigBody);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var get = await client.GetAsync("/api/claims-mapping/token-config?clientId=cm-test-client");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var rules = await client.GetAsync("/api/claims-mapping/rules?clientId=cm-test-client");
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task Admin_KeepsExistingBounds(string role)
    {
        var r = await ClientWith(role).PutAsJsonAsync("/api/claims-mapping/token-config", new
        {
            clientId = "cm-test-client", accessTokenLifetimeMinutes = 1441, refreshTokenLifetimeDays = 7
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task RejectedPut_LeavesStoredConfigurationUnchanged()
    {
        var admin = ClientWith("SystemAdmin");
        var cid = $"cm-unchanged-{Guid.NewGuid():N}";
        var ok = await admin.PutAsJsonAsync("/api/claims-mapping/token-config", new
        {
            clientId = cid, accessTokenLifetimeMinutes = 30, refreshTokenLifetimeDays = 10, includeRoles = true
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var denied = await ClientWith("User").PutAsJsonAsync("/api/claims-mapping/token-config", new
        {
            clientId = cid, accessTokenLifetimeMinutes = 1440, refreshTokenLifetimeDays = 365, includeRoles = true
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var body = await (await admin.GetAsync($"/api/claims-mapping/token-config?clientId={cid}"))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(30, body.GetProperty("accessTokenLifetimeMinutes").GetInt32());
        Assert.Equal(10, body.GetProperty("refreshTokenLifetimeDays").GetInt32());
    }
}
