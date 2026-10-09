using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5002: a SuperAdmin asking for offline_access got an empty HTTP 500 at the token endpoint ("Conflicting
/// destinations for the claim 'role'"). SuperAdminClaimsTransformation also runs on the principal the token endpoint
/// reads from the authorization code and adds the roles SuperAdmin implies, which carry no destinations, so
/// OpenIddict cannot build the refresh token. These tests drive the REAL pipeline in-process (login ->
/// /connect/authorize -> /connect/token -> refresh_token), same in-memory-RSA harness as RefreshTokenRoleRecheckTests,
/// and assert that tokens carry exactly the stored roles (the implied admin roles never leak into a token).
/// </summary>
public class SuperAdminOfflineAccessTests : IClassFixture<SuperAdminOfflineAccessTests.Factory>
{
    private const string RedirectUri = "https://app.example.test/callback";
    private const string Password = "Str0ngTestPassw0rd!5002";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key-5002" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key-5002" };

    /// <summary>The roles SuperAdmin implies (SuperAdminClaimsTransformation); none may ever appear in a token.</summary>
    private static readonly string[] ImpliedRoles =
    {
        "SystemAdmin", "SecurityAdmin", "ComplianceOfficer", "TenantAdmin", "BuildingOwner", "BuildingManager", "EmergencyAccess"
    };

    public sealed class Factory : IAMTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIddictServerOptions>(options =>
                {
                    options.SigningCredentials.Clear();
                    options.SigningCredentials.Add(new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
                    options.EncryptionCredentials.Clear();
                    options.EncryptionCredentials.Add(new EncryptingCredentials(
                        EncryptionKey, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
                });
            });
        }
    }

    private readonly Factory _factory;

    public SuperAdminOfflineAccessTests(Factory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------------

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private sealed record Seeded(string ClientId, string Email, string[] ExpectedRoles);

    /// <summary>
    /// Seeds a client and a user holding <paramref name="roleNames"/>. App roles (names with ':') get Category
    /// "app:{client}", which also turns the federated app-role gate on for that client; a role listed in
    /// <paramref name="twoTenantRoles"/> is assigned at two tenants. Role names are made unique per test where they
    /// are not the well-known SuperAdmin.
    /// </summary>
    private async Task<Seeded> SeedAsync(string[] roleNames, string[]? twoTenantRoles = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var clientId = $"app{suffix}";
        var email = $"user-{suffix}@offline-access.test";
        var userId = Guid.NewGuid();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        db.Tenants.AddRange(
            new Tenant { Id = tenantA, Name = $"A {suffix}", Type = "Organization", IsActive = true },
            new Tenant { Id = tenantB, Name = $"B {suffix}", Type = "Organization", IsActive = true });
        db.Users.Add(new User
        {
            Id = userId, Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "Offline", LastName = "Access", EmailConfirmed = true, IsActive = true
        });

        var expected = new List<string>();
        foreach (var name in roleNames)
        {
            var isApp = name.Contains(':');
            var fullName = isApp ? $"{clientId}:{name.Split(':')[1]}" : name;
            var role = name == "SuperAdmin"
                ? await db.Roles.FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                : null;
            if (role == null)
            {
                role = new Role
                {
                    Id = Guid.NewGuid(), Name = fullName, TenantId = null, IsSystemRole = false,
                    Category = isApp ? $"app:{clientId}" : null
                };
                db.Roles.Add(role);
            }

            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenantA, GrantedAt = DateTime.UtcNow });
            if (twoTenantRoles?.Contains(name) == true)
                db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, TenantId = tenantB, GrantedAt = DateTime.UtcNow });
            expected.Add(role.Name);
        }
        await db.SaveChangesAsync();

        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Offline access test client",
            RedirectUris = { new Uri(RedirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                $"{Permissions.Prefixes.Scope}{Scopes.OpenId}",
                $"{Permissions.Prefixes.Scope}{Scopes.Profile}",
                $"{Permissions.Prefixes.Scope}{Scopes.Email}",
                $"{Permissions.Prefixes.Scope}{Scopes.Roles}",
                $"{Permissions.Prefixes.Scope}{Scopes.OfflineAccess}"
            }
        });

        return new Seeded(clientId, email, expected.Distinct().ToArray());
    }

    /// <summary>Login + authorize + redeem the code; returns the raw token endpoint response.</summary>
    private static async Task<(HttpResponseMessage Response, string Body)> RedeemCodeAsync(HttpClient client, Seeded user, string scope)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password });
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"Login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        var authorizeUrl = "/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(user.ClientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=" + Uri.EscapeDataString(scope) +
            "&state=xyz";
        var authorize = await client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), $"No authorization code in redirect: {authorize.Headers.Location}");

        var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = user.ClientId
        }));
        return (token, await token.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, Seeded user, string refreshToken) =>
        client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = user.ClientId
        }));

    private static JsonElement DecodePayload(string jwt) =>
        JsonSerializer.Deserialize<JsonElement>(Base64UrlEncoder.DecodeBytes(jwt.Split('.')[1]));

    /// <summary>The "role" claim is a bare string for one role, an array for several.</summary>
    private static string[] GetRoles(JsonElement payload)
    {
        if (!payload.TryGetProperty("role", out var role))
            return Array.Empty<string>();
        return role.ValueKind == JsonValueKind.Array
            ? role.EnumerateArray().Select(r => r.GetString()!).ToArray()
            : new[] { role.GetString()! };
    }

    private static void AssertExactlyStoredRoles(string[] expected, string[] actual)
    {
        Assert.Equal(expected.OrderBy(r => r, StringComparer.Ordinal), actual.OrderBy(r => r, StringComparer.Ordinal));
        Assert.Equal(actual.Length, actual.Distinct().Count());
    }

    // ----- tests -----------------------------------------------------------------------------------

    public static IEnumerable<object[]> Scenarios() => new[]
    {
        new object[] { "SuperAdmin alone", new[] { "SuperAdmin" }, Array.Empty<string>() },
        new object[] { "SuperAdmin plus app roles", new[] { "SuperAdmin", "app:admin", "app:member" }, Array.Empty<string>() },
        new object[] { "several ordinary app roles", new[] { "app:customer", "app:admin", "app:viewer" }, Array.Empty<string>() },
        new object[] { "one role at two tenants", new[] { "app:customer" }, new[] { "app:customer" } },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task CodeGrant_WithOfflineAccess_ReturnsARefreshToken_WithExactlyTheStoredRoles(string scenario, string[] roles, string[] twoTenantRoles)
    {
        var user = await SeedAsync(roles, twoTenantRoles);
        var client = NewClient();

        var (response, body) = await RedeemCodeAsync(client, user, "openid roles offline_access");

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{scenario}: token endpoint returned {(int)response.StatusCode} {body}");
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.False(string.IsNullOrEmpty(json.GetProperty("refresh_token").GetString()), $"{scenario}: no refresh_token");
        var accessRoles = GetRoles(DecodePayload(json.GetProperty("access_token").GetString()!));
        AssertExactlyStoredRoles(user.ExpectedRoles, accessRoles);
        Assert.Empty(accessRoles.Intersect(ImpliedRoles.Except(user.ExpectedRoles)));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task RefreshGrant_ReturnsANewAccessToken_WithTheSameRoles(string scenario, string[] roles, string[] twoTenantRoles)
    {
        var user = await SeedAsync(roles, twoTenantRoles);
        var client = NewClient();
        var (_, firstBody) = await RedeemCodeAsync(client, user, "openid roles offline_access");
        var first = JsonSerializer.Deserialize<JsonElement>(firstBody);

        var refresh = await RefreshAsync(client, user, first.GetProperty("refresh_token").GetString()!);

        var refreshBody = await refresh.Content.ReadAsStringAsync();
        Assert.True(refresh.StatusCode == HttpStatusCode.OK, $"{scenario}: refresh returned {(int)refresh.StatusCode} {refreshBody}");
        var second = JsonSerializer.Deserialize<JsonElement>(refreshBody);
        var firstRoles = GetRoles(DecodePayload(first.GetProperty("access_token").GetString()!));
        var secondRoles = GetRoles(DecodePayload(second.GetProperty("access_token").GetString()!));
        AssertExactlyStoredRoles(user.ExpectedRoles, secondRoles);
        Assert.Equal(firstRoles.OrderBy(r => r, StringComparer.Ordinal), secondRoles.OrderBy(r => r, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task CodeGrant_WithoutOfflineAccess_StillWorks_WithExactlyTheStoredRoles(string scenario, string[] roles, string[] twoTenantRoles)
    {
        var user = await SeedAsync(roles, twoTenantRoles);

        var (response, body) = await RedeemCodeAsync(NewClient(), user, "openid roles");

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{scenario}: token endpoint returned {(int)response.StatusCode} {body}");
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.False(json.TryGetProperty("refresh_token", out _), $"{scenario}: refresh_token without offline_access");
        AssertExactlyStoredRoles(user.ExpectedRoles, GetRoles(DecodePayload(json.GetProperty("access_token").GetString()!)));
    }
}
