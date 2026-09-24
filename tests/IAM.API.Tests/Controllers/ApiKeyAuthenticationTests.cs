using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hazina.Security.ApiKeys;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// JengoWork task 3911: IAM authenticates X-Api-Key through the shared Hazina.Security.ApiKeys middleware
/// (its own ApiKeyAuthenticationMiddleware is gone). These tests drive the real pipeline over HTTP with keys
/// minted by the real IApiKeyService: scope policies, tenant isolation, revocation/expiry, per-key rate limiting,
/// the audit trail, and the introspection endpoint the other Jengo apps validate against.
/// </summary>
public class ApiKeyAuthenticationTests : IClassFixture<ApiKeyAuthenticationTests.AuditingFactory>
{
    /// <summary>The normal test host plus an in-memory audit sink, so the trail can be asserted on.</summary>
    public sealed class AuditingFactory : IAMTestWebApplicationFactory
    {
        public CapturingSink Sink { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IApiKeyAuditSink>(Sink));
        }
    }

    public sealed class CapturingSink : IApiKeyAuditSink
    {
        private readonly List<ApiKeyAuditEntry> _entries = new();

        public IReadOnlyList<ApiKeyAuditEntry> For(string keyId)
        {
            lock (_entries) return _entries.Where(e => e.KeyId == keyId).ToList();
        }

        public IReadOnlyList<ApiKeyAuditEntry> All()
        {
            lock (_entries) return _entries.ToList();
        }

        public Task WriteAsync(ApiKeyAuditEntry entry, CancellationToken ct = default)
        {
            lock (_entries) _entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private readonly AuditingFactory _factory;

    public ApiKeyAuthenticationTests(AuditingFactory factory) => _factory = factory;

    private async Task<(Guid Id, string Raw)> CreateKeyAsync(
        string scope = "read", Guid? tenantId = null, int? rateLimit = null, DateTime? expiresAt = null)
    {
        using var scoped = _factory.Services.CreateScope();
        var service = scoped.ServiceProvider.GetRequiredService<IApiKeyService>();
        var (key, raw) = await service.CreateApiKeyAsync(
            $"t3911-{Guid.NewGuid():N}"[..20], userId: null, tenantId: tenantId, permissions: null,
            expiresAt: expiresAt, rateLimitPerMinute: rateLimit, description: null, scope: scope);
        return (key.Id, raw);
    }

    private HttpClient ClientWith(string rawKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", rawKey);
        return client;
    }

    private static string Slug() => $"app{Guid.NewGuid():N}"[..12];

    // ---- scopes -----------------------------------------------------------------------------

    [Fact]
    public async Task ReadKey_CanRead_ButNotWrite()
    {
        var (_, raw) = await CreateKeyAsync("read");
        var client = ClientWith(raw);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/app-roles/{Slug()}/users")).StatusCode);

        var write = await client.PostAsJsonAsync("/api/app-roles/register",
            new { clientId = Slug(), roles = new[] { new { name = "member", description = "x" } } });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task WriteKey_CanRegisterRoles()
    {
        var (_, raw) = await CreateKeyAsync("write");
        var clientId = Slug();

        var response = await ClientWith(raw).PostAsJsonAsync("/api/app-roles/register",
            new { clientId, roles = new[] { new { name = "member", description = "x" } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WriteKey_IsForbidden_OnAnAdminEndpoint()
    {
        var (_, raw) = await CreateKeyAsync("write");

        var response = await ClientWith(raw).PostAsJsonAsync("/api/api-keys/introspect", new { keyHash = new string('a', 64) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- tenant isolation ---------------------------------------------------------------------

    [Fact]
    public async Task TenantKey_AddressingAnotherTenant_Returns403_AndIsAudited()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (id, raw) = await CreateKeyAsync("admin", tenantA);

        var response = await ClientWith(raw).GetAsync($"/api/app-roles/{Slug()}/users?tenantId={tenantB}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entry = Assert.Single(_factory.Sink.For(id.ToString("D")));
        Assert.Equal(ApiKeyAuditOutcome.TenantMismatch, entry.Outcome);
        Assert.Equal(tenantA.ToString("D"), entry.TenantId);
    }

    [Fact]
    public async Task TenantKey_CannotReachTheGlobalAppRoleEndpoints()
    {
        // The app-role catalog is global and its user list spans tenants: a tenant key fails closed, whatever its scope.
        var (_, raw) = await CreateKeyAsync("admin", Guid.NewGuid());
        var client = ClientWith(raw);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/app-roles/{Slug()}/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/app-roles/{Slug()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/app-roles/register",
            new { clientId = Slug(), roles = new[] { new { name = "member", description = "x" } } })).StatusCode);
    }

    // ---- revoked / expired --------------------------------------------------------------------

    [Fact]
    public async Task RevokedKey_Returns401_ImmediatelyInThisProcess()
    {
        var (id, raw) = await CreateKeyAsync("read");
        var client = ClientWith(raw);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/app-roles/{Slug()}/users")).StatusCode);

        using (var scoped = _factory.Services.CreateScope())
            Assert.True(await scoped.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeApiKeyAsync(id));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/app-roles/{Slug()}/users")).StatusCode);
        Assert.Contains(_factory.Sink.For(id.ToString("D")), e => e.Outcome == ApiKeyAuditOutcome.Revoked);
    }

    [Fact]
    public async Task ExpiredKey_Returns401()
    {
        var (id, raw) = await CreateKeyAsync("read", expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var response = await ClientWith(raw).GetAsync($"/api/app-roles/{Slug()}/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(_factory.Sink.For(id.ToString("D")), e => e.Outcome == ApiKeyAuditOutcome.Expired);
    }

    // ---- rate limiting ------------------------------------------------------------------------

    [Fact]
    public async Task RateLimit_IsPerKey_AndAnswers429WithRetryAfter()
    {
        var (limitedId, limitedRaw) = await CreateKeyAsync("read", rateLimit: 2);
        var (_, otherRaw) = await CreateKeyAsync("read", rateLimit: 2);
        var limited = ClientWith(limitedRaw);
        var url = $"/api/app-roles/{Slug()}/users";

        Assert.Equal(HttpStatusCode.OK, (await limited.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await limited.GetAsync(url)).StatusCode);
        var throttled = await limited.GetAsync(url);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.Contains("Retry-After"));
        Assert.Contains(_factory.Sink.For(limitedId.ToString("D")), e => e.Outcome == ApiKeyAuditOutcome.RateLimited);

        // Another key has its own budget.
        Assert.Equal(HttpStatusCode.OK, (await ClientWith(otherRaw).GetAsync(url)).StatusCode);
    }

    // ---- audit ----------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRequest_IsAudited_WithPrefixTenantScopeEndpoint_AndNeverTheRawKey()
    {
        var (id, raw) = await CreateKeyAsync("read");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await ClientWith(raw).GetAsync($"/api/app-roles/{Slug()}/users");
        await ClientWith(raw + "tampered").GetAsync($"/api/app-roles/{Slug()}/users");

        var entry = Assert.Single(_factory.Sink.For(id.ToString("D")));
        Assert.Equal(ApiKeyAuditOutcome.Authenticated, entry.Outcome);
        Assert.StartsWith("iam_", entry.KeyPrefix);
        Assert.Equal("read", entry.Scope);
        Assert.Equal("GET", entry.Method);
        Assert.Contains("api/app-roles/{clientId}/users", entry.Endpoint);
        Assert.Equal(200, entry.StatusCode);
        Assert.True(entry.TimestampUtc >= before);

        // The tampered key was audited too (as an invalid key) and nothing carries either raw value.
        Assert.Contains(_factory.Sink.All(), e => e.Outcome == ApiKeyAuditOutcome.InvalidKey);
        var everything = JsonSerializer.Serialize(_factory.Sink.All());
        Assert.DoesNotContain(raw, everything);
    }

    // ---- storage: hash in the database, raw key in Vault -----------------------------------------

    [Fact]
    public async Task Database_HoldsOnlyTheHash_AndTheRawKeyIsInVault()
    {
        var (id, raw) = await CreateKeyAsync("write");

        using var scoped = _factory.Services.CreateScope();
        var db = scoped.ServiceProvider.GetRequiredService<IAMDbContext>();
        var entity = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == id);

        Assert.Equal(ApiKeyHasher.Hash(raw), entity.KeyHash);
        Assert.Equal("write", entity.Scope);
        foreach (var property in typeof(ApiKey).GetProperties().Where(p => p.PropertyType == typeof(string)))
            Assert.DoesNotContain(raw, (string?)property.GetValue(entity) ?? string.Empty);

        // The development host uses the in-memory vault; production uses the Prospergenics vault.
        var vault = Assert.IsType<InMemoryApiKeySecretVault>(_factory.Services.GetRequiredService<IApiKeySecretVault>());
        Assert.NotNull(entity.VaultReference);
        Assert.Equal(raw, vault.Secrets[entity.VaultReference!].RawKey);
    }

    // ---- introspection (what the other Jengo apps validate against) -------------------------------

    [Fact]
    public async Task Introspect_ReturnsTheKeyForItsHash_ToAPlatformAdminKey()
    {
        var tenant = Guid.NewGuid();
        var (id, raw) = await CreateKeyAsync("write", tenant);
        var (_, adminRaw) = await CreateKeyAsync("admin");

        var response = await ClientWith(adminRaw).PostAsJsonAsync("/api/api-keys/introspect", new { keyHash = ApiKeyHasher.Hash(raw) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id.ToString("D"), body.GetProperty("keyId").GetString());
        Assert.Equal("write", body.GetProperty("scope").GetString());
        Assert.Equal(tenant.ToString("D"), body.GetProperty("tenantId").GetString());
        Assert.True(body.GetProperty("active").GetBoolean());
        Assert.DoesNotContain(raw, body.GetRawText());
    }

    [Fact]
    public async Task Introspect_ReportsARevokedKeyAsInactive_AndAnUnknownHashAs404()
    {
        var (id, raw) = await CreateKeyAsync("read");
        using (var scoped = _factory.Services.CreateScope())
            await scoped.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeApiKeyAsync(id);
        var (_, adminRaw) = await CreateKeyAsync("admin");
        var admin = ClientWith(adminRaw);

        var revoked = await admin.PostAsJsonAsync("/api/api-keys/introspect", new { keyHash = ApiKeyHasher.Hash(raw) });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.False((await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("active").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync("/api/api-keys/introspect", new { keyHash = ApiKeyHasher.Hash("no-such-key") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/api-keys/introspect", new { keyHash = "not-a-hash" })).StatusCode);
    }

    [Fact]
    public async Task Introspect_IsRefusedToATenantScopedAdminKey_SoItCannotProbeOtherTenantsKeys()
    {
        var (_, victimRaw) = await CreateKeyAsync("read", Guid.NewGuid());
        var (_, tenantAdminRaw) = await CreateKeyAsync("admin", Guid.NewGuid());

        var response = await ClientWith(tenantAdminRaw)
            .PostAsJsonAsync("/api/api-keys/introspect", new { keyHash = ApiKeyHasher.Hash(victimRaw) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task HttpApiKeyLookup_FromAnotherApp_ResolvesKeysAgainstThisIam()
    {
        // The consumer side of the hybrid design: Hazina's own HttpApiKeyLookup (what jengo-mcp/TaskManager use)
        // talking to IAM's real introspection endpoint with a platform admin service key.
        var tenant = Guid.NewGuid();
        var (id, raw) = await CreateKeyAsync("write", tenant);
        var (_, serviceRaw) = await CreateKeyAsync("admin");
        var lookup = new HttpApiKeyLookup(
            new SingleClientFactory(ClientWith(serviceRaw)),
            Microsoft.Extensions.Options.Options.Create(new HttpApiKeyLookupOptions { BaseUrl = "http://localhost/" }));

        var record = await lookup.FindByHashAsync(ApiKeyHasher.Hash(raw));

        Assert.NotNull(record);
        Assert.Equal(id.ToString("D"), record!.Id);
        Assert.Equal(ApiKeyScope.Write, record.Scope);
        Assert.Equal(tenant.ToString("D"), record.TenantId);
        Assert.True(record.IsActive);
        Assert.Null(await lookup.FindByHashAsync(ApiKeyHasher.Hash("no-such-key")));
    }

    [Fact]
    public async Task Introspect_WithoutAKey_IsRefused()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/api-keys/introspect", new { keyHash = new string('a', 64) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
