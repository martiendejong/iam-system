using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4701: POST /api/api-keys follows the caller's real authority, end to end (JWT user -> controller ->
/// real service -> UserRoles). Platform-wide keys need SuperAdmin/SystemAdmin; a tenant key needs
/// SuperAdmin/SystemAdmin or a TenantAdmin role scoped to that tenant; permissions and rate limit are validated.
/// API-key callers and the admin-scope rule are covered by <see cref="ApiKeyIssueGuardTests"/>.
/// </summary>
public class ApiKeyIssueAuthorityTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private readonly IAMTestWebApplicationFactory _factory;

    public ApiKeyIssueAuthorityTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private async Task<Guid> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>A user with a TenantAdmin role row in each given tenant (a null entry = unscoped row).</summary>
    private async Task<Guid> CreateTenantAdminAsync(DateTime? expiresAt, params Guid?[] tenants)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@apikey-authz.test", PasswordHash = "x" };
        db.Users.Add(user);
        foreach (var t in tenants)
        {
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(), UserId = user.Id, RoleId = TenantAdminRoleId, TenantId = t, ExpiresAt = expiresAt,
                GrantedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    private Task<Guid> CreateTenantAdminAsync(params Guid?[] tenants) => CreateTenantAdminAsync(null, tenants);

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@apikey-authz.test", roles, tenantClaim);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object Body(Guid? tenantId, string? scope = null, string[]? permissions = null, int? rate = null) => new
    {
        name = $"key-{Guid.NewGuid():N}"[..16],
        tenantId,
        scope,
        permissions,
        rateLimitPerMinute = rate
    };

    private static Task<HttpResponseMessage> Post(HttpClient c, object body) => c.PostAsJsonAsync("/api/api-keys", body);

    [Fact]
    public async Task PlainUser_Gets403_ForTenantKeyAndPlatformKey()
    {
        var tenant = await CreateTenantAsync();
        var client = ClientAs(Guid.NewGuid(), new[] { "User" });

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, Body(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, Body(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, Body(tenant, "write"))).StatusCode);
    }

    [Fact]
    public async Task Anonymous_Gets401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(_factory.CreateClient(), Body(null))).StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_CanIssueReadAndWriteKeys_ForOwnTenant()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant);
        var client = ClientAs(admin, new[] { "TenantAdmin" });

        var read = await Post(client, Body(tenant, "read"));
        var write = await Post(client, Body(tenant, "write"));

        Assert.Equal(HttpStatusCode.Created, read.StatusCode);
        Assert.Equal(HttpStatusCode.Created, write.StatusCode);
        Assert.Equal(tenant, (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task TenantAdmin_CannotIssueForAnotherTenant()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own);

        var response = await Post(ClientAs(admin, new[] { "TenantAdmin" }), Body(other));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_OmittingTheTenant_GetsTheirOwn_NeverPlatformWide()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant);

        var response = await Post(ClientAs(admin, new[] { "TenantAdmin" }), Body(null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(tenant, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task TenantAdminOfSeveralTenants_MustNameOne()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(a, b);
        var client = ClientAs(admin, new[] { "TenantAdmin" });

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, Body(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, Body(b))).StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_CannotIssueAdminScope()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant);

        var response = await Post(ClientAs(admin, new[] { "TenantAdmin" }), Body(tenant, "admin"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_WithTokenScopedToAnotherTenant_IsForbidden()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own);

        var response = await Post(ClientAs(admin, new[] { "TenantAdmin" }, tenantClaim: other.ToString()), Body(own));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredOrUnscopedTenantAdminRole_ConfersNothing()
    {
        var tenant = await CreateTenantAsync();
        var expired = await CreateTenantAdminAsync(DateTime.UtcNow.AddMinutes(-5), tenant);
        var unscoped = await CreateTenantAdminAsync(new Guid?[] { null });

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(ClientAs(expired, new[] { "TenantAdmin" }), Body(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(ClientAs(unscoped, new[] { "TenantAdmin" }), Body(tenant))).StatusCode);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task GlobalAdmin_CanIssuePlatformTenantAndAdminKeys(string role)
    {
        var tenant = await CreateTenantAsync();
        var client = ClientAs(Guid.NewGuid(), new[] { role });

        Assert.Equal(HttpStatusCode.Created, (await Post(client, Body(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, Body(tenant, "write"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, Body(null, "admin"))).StatusCode);
    }

    // ----- validation (service layer) ----------------------------------------------------------

    [Theory]
    [InlineData("*")]
    [InlineData("users:*")]
    [InlineData("Users:Create")]
    [InlineData("nocolon")]
    [InlineData("")]
    public async Task InvalidPermission_IsBadRequest(string permission)
    {
        var client = ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, Body(null, permissions: new[] { permission }))).StatusCode);
    }

    [Fact]
    public async Task TooManyPermissions_IsBadRequest()
    {
        var many = Enumerable.Range(0, ApiKeyIssueRules.MaxPermissions + 1).Select(i => $"res{i}:read").ToArray();

        var response = await Post(ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" }), Body(null, permissions: many));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(ApiKeyIssueRules.MaxRateLimitPerMinute + 1)]
    [InlineData(1000000)]
    public async Task OutOfRangeRateLimit_IsBadRequest(int rate)
    {
        var response = await Post(ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" }), Body(null, rate: rate));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ValidPermissionsAndRateLimit_AreAccepted()
    {
        var response = await Post(ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" }),
            Body(null, permissions: new[] { "users:create", "invitations:send" }, rate: ApiKeyIssueRules.MaxRateLimitPerMinute));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Service_RejectsBadInput_EvenWithoutTheController()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IApiKeyService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateApiKeyAsync("k", null, null, permissions: new List<string> { "*" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateApiKeyAsync("k", null, null, rateLimitPerMinute: 999999));
    }
}
