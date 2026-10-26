using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4702: every /api/webhooks endpoint needs SuperAdmin or an administrator of the subscription's tenant,
/// and a webhook URL must be a public address. Deliveries use a stub HTTP client (no network).
/// </summary>
public class WebhookTenantAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public WebhookTenantAuthorizationTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IHttpClientFactory>(new StubFactory());
        }));
    }

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<Guid> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> CreateTenantAdminAsync(Guid tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@webhook-authz.test", PasswordHash = "x" };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), UserId = user.Id, RoleId = TenantAdminRoleId, TenantId = tenantId,
            ExpiresAt = expiresAt, GrantedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> CreateSubscriptionAsync(Guid tenantId, string url = "http://93.184.216.34/hook")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), Name = "seeded", Url = url, TenantId = tenantId, Events = "[\"*\"]",
            IsActive = true, ContentType = "application/json", MaxRetries = 0, TimeoutSeconds = 5
        };
        db.Set<WebhookSubscription>().Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private async Task<bool> SubscriptionExistsAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Set<WebhookSubscription>().AsNoTracking().AnyAsync(s => s.Id == id);
    }

    private HttpClient ClientAs(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@webhook-authz.test", roles, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient PlainUser() => ClientAs(Guid.NewGuid(), "User");
    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), "SuperAdmin");
    private HttpClient TenantAdminOf(Guid userId) => ClientAs(userId, "TenantAdmin");

    private static object CreateBody(Guid tenantId, string url = "https://93.184.216.34/hook") => new
    {
        name = "hook", url, tenantId, events = new[] { "user.created" }
    };

    private static async Task<HttpStatusCode[]> AllEndpointsAsync(HttpClient c, Guid tenantId, Guid subscriptionId) => new[]
    {
        (await c.PostAsJsonAsync("/api/webhooks", CreateBody(tenantId))).StatusCode,
        (await c.GetAsync($"/api/webhooks?tenantId={tenantId}")).StatusCode,
        (await c.GetAsync($"/api/webhooks/{subscriptionId}")).StatusCode,
        (await c.PutAsJsonAsync($"/api/webhooks/{subscriptionId}", new { name = "renamed" })).StatusCode,
        (await c.PostAsync($"/api/webhooks/{subscriptionId}/test", null)).StatusCode,
        (await c.GetAsync($"/api/webhooks/{subscriptionId}/deliveries")).StatusCode,
        (await c.DeleteAsync($"/api/webhooks/{subscriptionId}")).StatusCode,
    };

    // ----- 403 for everyone who is not SuperAdmin / admin of the tenant ------------------------

    [Fact]
    public async Task PlainUser_Gets403_OnEveryEndpoint_AndNothingChanges()
    {
        var tenant = await CreateTenantAsync();
        var sub = await CreateSubscriptionAsync(tenant);

        var statuses = await AllEndpointsAsync(PlainUser(), tenant, sub);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Forbidden, s));
        Assert.True(await SubscriptionExistsAsync(sub));
    }

    [Fact]
    public async Task TenantAdminOfAnotherTenant_Gets403_OnEveryEndpoint_AndNothingChanges()
    {
        var own = await CreateTenantAsync();
        var victim = await CreateTenantAsync();
        var sub = await CreateSubscriptionAsync(victim);
        var admin = await CreateTenantAdminAsync(own);

        var statuses = await AllEndpointsAsync(TenantAdminOf(admin), victim, sub);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Forbidden, s));
        Assert.True(await SubscriptionExistsAsync(sub));
    }

    [Fact]
    public async Task PlainUser_Gets403_ForUnknownIds_NotNotFound()
    {
        var response = await PlainUser().GetAsync($"/api/webhooks/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Gets401()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/webhooks?tenantId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredTenantAdminRole_Gets403()
    {
        var tenant = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(tenant, DateTime.UtcNow.AddMinutes(-5));

        var response = await TenantAdminOf(admin).GetAsync($"/api/webhooks?tenantId={tenant}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantClaimForAnotherTenant_Gets403()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var admin = await CreateTenantAdminAsync(own);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateJwtToken(admin, "a@webhook-authz.test", new[] { "TenantAdmin" }, other.ToString()));

        var response = await client.GetAsync($"/api/webhooks?tenantId={own}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ServiceAccountToken_Gets403()
    {
        var tenant = await CreateTenantAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateServiceAccountToken("svc-client", "webhooks:manage"));

        var response = await client.GetAsync($"/api/webhooks?tenantId={tenant}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- own-tenant admins and SuperAdmin still work -----------------------------------------

    [Fact]
    public async Task TenantAdmin_CanUseEveryEndpoint_ForOwnTenant()
    {
        var tenant = await CreateTenantAsync();
        var sub = await CreateSubscriptionAsync(tenant);
        var client = TenantAdminOf(await CreateTenantAdminAsync(tenant));

        var created = await client.PostAsJsonAsync("/api/webhooks", CreateBody(tenant));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/webhooks?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/webhooks/{sub}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/webhooks/{sub}", new { name = "renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/webhooks/{sub}/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/webhooks/{sub}/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/webhooks/{sub}")).StatusCode);
        Assert.False(await SubscriptionExistsAsync(sub));
    }

    [Fact]
    public async Task SuperAdmin_CanManageAnyTenant()
    {
        var tenant = await CreateTenantAsync();
        var sub = await CreateSubscriptionAsync(tenant);
        var client = SuperAdmin();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/webhooks", CreateBody(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/webhooks/{sub}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/webhooks/{sub}")).StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_UnknownId_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SuperAdmin().GetAsync($"/api/webhooks/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task EventTypes_StaysOpenToAnyAuthenticatedUser()
    {
        Assert.Equal(HttpStatusCode.OK, (await PlainUser().GetAsync("/api/webhooks/event-types")).StatusCode);
    }

    // ----- SSRF through the API ----------------------------------------------------------------

    [Theory]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://localhost/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://[::1]/")]
    [InlineData("ftp://93.184.216.34/")]
    public async Task Create_WithBlockedUrl_AsOwnTenantAdmin_Is400(string url)
    {
        var tenant = await CreateTenantAsync();
        var client = TenantAdminOf(await CreateTenantAdminAsync(tenant));

        var response = await client.PostAsJsonAsync("/api/webhooks", CreateBody(tenant, url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForAnotherTenant_Is403_EvenWithABlockedUrl()
    {
        var own = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var client = TenantAdminOf(await CreateTenantAdminAsync(own));

        var response = await client.PostAsJsonAsync("/api/webhooks", CreateBody(other, "http://169.254.169.254/"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_ToABlockedUrl_Is400_AndTheUrlIsUnchanged()
    {
        var tenant = await CreateTenantAsync();
        var sub = await CreateSubscriptionAsync(tenant);
        var client = TenantAdminOf(await CreateTenantAdminAsync(tenant));

        var response = await client.PutAsJsonAsync($"/api/webhooks/{sub}", new { url = "http://169.254.169.254/" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await (await client.GetAsync($"/api/webhooks/{sub}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("http://93.184.216.34/hook", body.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Test_OnAnOldRowWithABlockedUrl_ReturnsAFailedDeliveryWithoutBody()
    {
        var tenant = await CreateTenantAsync();
        var sub = await CreateSubscriptionAsync(tenant, "http://169.254.169.254/latest/meta-data/");
        var client = SuperAdmin();

        var response = await client.PostAsync($"/api/webhooks/{sub}/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("responseBody").ValueKind);
    }
}
