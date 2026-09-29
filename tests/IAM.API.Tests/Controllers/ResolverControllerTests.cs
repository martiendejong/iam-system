using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IAM.API.Controllers;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4059: who-decides resolver tests.
///
/// All tests use the OpenIddict validation pipeline (not the test-helper HS256 JWT bearer),
/// so the vault client goes through the real /connect/token → at+jwt flow.
/// The RSA keys are swapped to in-memory keys (same pattern as AccessTokenFormatTests)
/// to avoid the Windows CNG "Keyset does not exist" failure on the CI host.
/// </summary>
public class ResolverControllerTests : IDisposable
{
    // --- constants shared across tests ---
    private const string VaultClientId = "test-vault-resolver";
    private const string VaultClientSecret = "vault-test-secret-4059";
    private static readonly Guid TenantId = Guid.Parse("11111111-4059-4059-4059-111111111111");

    // Principal IDs for the management hierarchy tests
    private static readonly Guid RootManagerId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000001");
    private static readonly Guid MidManagerId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000002");
    private static readonly Guid LeafUserId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000003");
    private static readonly Guid DisabledManagerId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000004");
    private static readonly Guid OtherTenantUserId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000005");
    private static readonly Guid ServiceAccountId = Guid.Parse("bbbbbbbb-4059-4059-4059-000000000001");
    private static readonly Guid GroupId = Guid.Parse("cccccccc-4059-4059-4059-000000000001");
    private static readonly Guid UserWithDisabledManagerId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000009");
    private static readonly Guid GroupOwnerId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000006");
    private static readonly Guid GroupAdminId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000007");
    private static readonly Guid GroupMemberId = Guid.Parse("aaaaaaaa-4059-4059-4059-000000000008");

    // RSA key pair shared between all factories in this test class
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "resolver-test-signing" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "resolver-test-encryption" };

    private readonly ResolverFactory _factory;
    private readonly HttpClient _client;

    public ResolverControllerTests()
    {
        _factory = new ResolverFactory();
        _client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        SeedAll(_factory).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ===========================
    //  Vault client auth tests
    // ===========================

    [Fact]
    public async Task NoToken_Returns401()
    {
        var resp = await _client.GetAsync($"/api/resolver/users/{LeafUserId}/chain");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task UserToken_Returns403()
    {
        // A HS256 JWT Bearer token (not an at+jwt, no iam_resolver scope) must be rejected.
        var bearerToken = TestAuthenticationHelper.GenerateJwtToken(
            Guid.NewGuid(), "other@test.com", new[] { "User" });

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/users/{LeafUserId}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var resp = await _client.SendAsync(req);
        // Either 401 (scheme mismatch) or 403 (authenticated but no scope) are acceptable
        Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403, got {resp.StatusCode}");
    }

    [Fact]
    public async Task VaultToken_WithResolverScope_Returns200()
    {
        var token = await GetVaultTokenAsync();
        Assert.False(string.IsNullOrEmpty(token));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/users/{LeafUserId}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ===========================
    //  User chain tests
    // ===========================

    [Fact]
    public async Task UserChain_UnknownUser_Returns404()
    {
        var token = await GetVaultTokenAsync();
        var unknown = Guid.NewGuid();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/users/{unknown}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task UserChain_NoManager_ReturnsEmptyChain()
    {
        // RootManagerId has no manager
        var token = await GetVaultTokenAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/users/{RootManagerId}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ParseBodyAsync(resp);
        Assert.Equal(0, body.GetProperty("chain").GetArrayLength());
        Assert.False(body.GetProperty("truncatedAt8Hops").GetBoolean());
    }

    [Fact]
    public async Task UserChain_TwoHops_ReturnsOrderedChain()
    {
        // LeafUser → MidManager → RootManager
        var token = await GetVaultTokenAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/users/{LeafUserId}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ParseBodyAsync(resp);
        var chain = body.GetProperty("chain");
        Assert.Equal(2, chain.GetArrayLength());

        // First hop = MidManager, second = RootManager
        Assert.Equal(MidManagerId.ToString(), chain[0].GetProperty("principalId").GetString());
        Assert.Equal("manager", chain[0].GetProperty("hopRole").GetString());
        Assert.Equal(RootManagerId.ToString(), chain[1].GetProperty("principalId").GetString());
        Assert.Equal("manager", chain[1].GetProperty("hopRole").GetString());
    }

    [Fact]
    public async Task UserChain_DirectManagerDisabled_SkipsAndContinues()
    {
        // UserWithDisabledManager has DisabledManager as direct manager.
        // DisabledManager is inactive but has ManagerUserId = RootManagerId.
        // The resolver must skip DisabledManager AND continue to surface RootManagerId.
        var token = await GetVaultTokenAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/users/{UserWithDisabledManagerId}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ParseBodyAsync(resp);
        var chain = body.GetProperty("chain");
        // DisabledManager is skipped (inactive); walk continues → RootManager (1 active hop)
        Assert.Equal(1, chain.GetArrayLength());
        Assert.Equal(RootManagerId.ToString(), chain[0].GetProperty("principalId").GetString());
    }

    [Fact]
    public async Task UserChain_StopsAtTenantBoundary()
    {
        // OtherTenantUser's manager is RootManager but they are in a DIFFERENT tenant.
        // The chain should be empty (manager is out of bounds from the start).
        var token = await GetVaultTokenAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/users/{OtherTenantUserId}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ParseBodyAsync(resp);
        // RootManager is in TenantId, OtherTenantUser is in a different tenant → chain stops
        Assert.Equal(0, body.GetProperty("chain").GetArrayLength());
    }

    // ===========================
    //  Service-account tests
    // ===========================

    [Fact]
    public async Task ServiceAccountChain_ReturnsManagerChain()
    {
        // ServiceAccount → MidManager → RootManager
        var token = await GetVaultTokenAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/service-accounts/{ServiceAccountId}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ParseBodyAsync(resp);
        var chain = body.GetProperty("chain");
        Assert.Equal(2, chain.GetArrayLength());
        Assert.Equal(MidManagerId.ToString(), chain[0].GetProperty("principalId").GetString());
        Assert.Equal(RootManagerId.ToString(), chain[1].GetProperty("principalId").GetString());
    }

    // ===========================
    //  Group-target tests
    // ===========================

    [Fact]
    public async Task GroupChain_ReturnOwnersBeforeAdmins()
    {
        var token = await GetVaultTokenAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/groups/{GroupId}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ParseBodyAsync(resp);
        var chain = body.GetProperty("chain");

        // Two entries: owner first, then admin (member not included)
        Assert.Equal(2, chain.GetArrayLength());
        Assert.Equal("group-owner", chain[0].GetProperty("hopRole").GetString());
        Assert.Equal(GroupOwnerId.ToString(), chain[0].GetProperty("principalId").GetString());
        Assert.Equal("group-admin", chain[1].GetProperty("hopRole").GetString());
        Assert.Equal(GroupAdminId.ToString(), chain[1].GetProperty("principalId").GetString());
    }

    [Fact]
    public async Task GroupChain_UnknownGroup_Returns404()
    {
        var token = await GetVaultTokenAsync();
        var unknown = Guid.NewGuid();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/resolver/groups/{unknown}/chain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===========================
    //  Helpers
    // ===========================

    private async Task<string> GetVaultTokenAsync()
    {
        var resp = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = VaultClientId,
                ["client_secret"] = VaultClientSecret,
                ["scope"] = ResolverController.ResolverScope
            }));

        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK,
            $"client_credentials failed: {resp.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonElement> ParseBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    // ===========================
    //  Test data seeding
    // ===========================

    private static async Task SeedAll(ResolverFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

        // Seed the iam_resolver scope so the token endpoint can issue it
        await scopes.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = ResolverController.ResolverScope,
            DisplayName = "IAM Resolver",
            Resources = { "iam_api" }
        });

        // Vault confidential client
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = VaultClientId,
            ClientSecret = VaultClientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Test vault resolver",
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                $"{Permissions.Prefixes.Scope}{ResolverController.ResolverScope}"
            }
        });

        var otherTenantId = Guid.Parse("22222222-4059-4059-4059-222222222222");

        // Tenants
        db.Tenants.AddRange(
            new Tenant { Id = TenantId, Name = "Test Tenant 4059", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = otherTenantId, Name = "Other Tenant 4059", IsActive = true, CreatedAt = DateTime.UtcNow });

        // Users in the management hierarchy (all in TenantId)
        db.Users.AddRange(
            new User { Id = RootManagerId, Email = "root@resolver.test", FirstName = "Root", LastName = "Manager", IsActive = true, EmailConfirmed = true, PrincipalKind = PrincipalKind.Human },
            new User { Id = MidManagerId, Email = "mid@resolver.test", FirstName = "Mid", LastName = "Manager", IsActive = true, EmailConfirmed = true, ManagerUserId = RootManagerId, PrincipalKind = PrincipalKind.Human },
            new User { Id = LeafUserId, Email = "leaf@resolver.test", FirstName = "Leaf", LastName = "User", IsActive = true, EmailConfirmed = true, ManagerUserId = MidManagerId, PrincipalKind = PrincipalKind.Human },
            new User { Id = DisabledManagerId, Email = "disabled@resolver.test", FirstName = "Disabled", LastName = "Manager", IsActive = false, EmailConfirmed = true, ManagerUserId = RootManagerId, PrincipalKind = PrincipalKind.Human },
            new User { Id = UserWithDisabledManagerId, Email = "hasdisabled@resolver.test", FirstName = "Has", LastName = "DisabledMgr", IsActive = true, EmailConfirmed = true, ManagerUserId = DisabledManagerId, PrincipalKind = PrincipalKind.Human },
            new User { Id = OtherTenantUserId, Email = "other@resolver.test", FirstName = "Other", LastName = "Tenant", IsActive = true, EmailConfirmed = true, ManagerUserId = RootManagerId, PrincipalKind = PrincipalKind.Human },
            new User { Id = GroupOwnerId, Email = "owner@resolver.test", FirstName = "Group", LastName = "Owner", IsActive = true, EmailConfirmed = true, PrincipalKind = PrincipalKind.Human },
            new User { Id = GroupAdminId, Email = "admin@resolver.test", FirstName = "Group", LastName = "Admin", IsActive = true, EmailConfirmed = true, PrincipalKind = PrincipalKind.Human },
            new User { Id = GroupMemberId, Email = "member@resolver.test", FirstName = "Group", LastName = "Member", IsActive = true, EmailConfirmed = true, PrincipalKind = PrincipalKind.Human });

        // Assign users to tenants via a role (UserRoles drive the tenant membership)
        var baseRole = new Role
        {
            Id = Guid.Parse("dddddddd-4059-0000-0000-000000000001"),
            Name = "test:member-4059",
            IsSystemRole = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Roles.Add(baseRole);

        foreach (var userId in new[] { RootManagerId, MidManagerId, LeafUserId, DisabledManagerId, UserWithDisabledManagerId, GroupOwnerId, GroupAdminId, GroupMemberId })
        {
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = baseRole.Id,
                TenantId = TenantId,
                GrantedAt = DateTime.UtcNow
            });
        }

        // OtherTenantUser is in a DIFFERENT tenant — their manager (RootManager) is in TenantId,
        // so the resolver must stop at the boundary.
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = OtherTenantUserId,
            RoleId = baseRole.Id,
            TenantId = otherTenantId,
            GrantedAt = DateTime.UtcNow
        });

        // Service account in TenantId → manager = MidManager
        db.ServiceAccounts.Add(new ServiceAccount
        {
            Id = ServiceAccountId,
            Name = "Test SA 4059",
            TenantId = TenantId,
            ClientId = "test-sa-4059",
            ClientSecretHash = "hash",
            ManagerUserId = MidManagerId,
            PrincipalKind = PrincipalKind.Service,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        // Group with owner, admin, and a plain member
        db.Groups.Add(new Group
        {
            Id = GroupId,
            Name = "Test Group 4059",
            TenantId = TenantId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.GroupMemberships.AddRange(
            new GroupMembership { Id = Guid.NewGuid(), GroupId = GroupId, UserId = GroupOwnerId, Role = "owner", IsActive = true },
            new GroupMembership { Id = Guid.NewGuid(), GroupId = GroupId, UserId = GroupAdminId, Role = "admin", IsActive = true },
            new GroupMembership { Id = Guid.NewGuid(), GroupId = GroupId, UserId = GroupMemberId, Role = "member", IsActive = true });

        await db.SaveChangesAsync();
    }

    // ===========================
    //  Test factory with RSA keys
    // ===========================

    private sealed class ResolverFactory : IAMTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIddictServerOptions>(options =>
                {
                    options.SigningCredentials.Clear();
                    options.SigningCredentials.Add(
                        new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
                    options.EncryptionCredentials.Clear();
                    options.EncryptionCredentials.Add(new EncryptingCredentials(
                        EncryptionKey, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
                });
            });
        }
    }
}
