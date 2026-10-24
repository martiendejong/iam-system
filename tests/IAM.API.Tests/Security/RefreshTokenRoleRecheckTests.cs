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
/// Task 4096: the refresh_token grant must not copy role/tenant_id claims from the refresh token
/// into the new access token — roles can be revoked while a refresh token is still valid (7 days,
/// sliding) and resource servers verify access tokens offline (task 3481). These tests drive the
/// REAL OpenIddict pipeline in-process (login -> /connect/authorize -> /connect/token -> refresh),
/// change role assignments in the database between login and refresh, and assert on the refreshed
/// token. Same in-memory-RSA key setup as AccessTokenFormatTests (the harness this extends): the
/// Development AddDevelopment*Certificate() path hits CNG "Keyset does not exist" on this host.
/// </summary>
public class RefreshTokenRoleRecheckTests
{
    private const string ClientId = "taskmanager";
    private const string RedirectUri = "https://taskmanager.example.test/callback";
    private const string UserEmail = "role-recheck@test.example";
    private const string UserPassword = "Str0ngTestPassw0rd!4096";
    private const string CustomerRoleName = "taskmanager:customer";
    private const string AdminRoleName = "taskmanager:admin";
    private static readonly Guid TenantId = Guid.Parse("33333333-4096-4096-4096-333333333333");
    private static readonly Guid UserId = Guid.Parse("44444444-4096-4096-4096-444444444444");
    private static readonly Guid CustomerRoleId = Guid.Parse("55555555-4096-4096-4096-555555555555");
    private static readonly Guid AdminRoleId = Guid.Parse("66666666-4096-4096-4096-666666666666");

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key-4096" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key-4096" };

    private sealed class RoleRecheckFactory : IAMTestWebApplicationFactory
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

    // ----- helpers ---------------------------------------------------------------------------------

    /// <summary>https: the IAM.Session cookie is Secure and the issuer is derived from the request URL.</summary>
    private static HttpClient NewClient(RoleRecheckFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    /// <summary>
    /// Seeds the taskmanager client WITH a role catalog (both roles carry Category "app:taskmanager",
    /// so the app-role gate is active) and a user holding customer + admin, both scoped to TenantId.
    /// </summary>
    private static async Task SeedAsync(RoleRecheckFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Role Recheck Tenant", Type = "Organization", IsActive = true });

        db.Roles.AddRange(
            new Role { Id = CustomerRoleId, Name = CustomerRoleName, Category = $"app:{ClientId}", TenantId = null, IsSystemRole = false },
            new Role { Id = AdminRoleId, Name = AdminRoleName, Category = $"app:{ClientId}", TenantId = null, IsSystemRole = false });

        db.Users.Add(new User
        {
            Id = UserId,
            Email = UserEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(UserPassword),
            FirstName = "Role",
            LastName = "Recheck",
            EmailConfirmed = true,
            IsActive = true
        });

        db.UserRoles.AddRange(
            new UserRole { Id = Guid.NewGuid(), UserId = UserId, RoleId = CustomerRoleId, TenantId = TenantId, GrantedAt = DateTime.UtcNow },
            new UserRole { Id = Guid.NewGuid(), UserId = UserId, RoleId = AdminRoleId, TenantId = TenantId, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "TaskManager (role recheck test client)",
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
    }

    /// <summary>Real browser-equivalent flow: login, authorize (cookie), exchange the code.</summary>
    private static async Task<JsonElement> LoginAndGetTokensAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = UserEmail, password = UserPassword });
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"Login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        var authorizeUrl = "/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(ClientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=" + Uri.EscapeDataString("openid profile email roles offline_access") +
            "&state=xyz";
        var authorize = await client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var location = authorize.Headers.Location!;
        Assert.StartsWith(RedirectUri, location.ToString());
        var code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), $"No authorization code in redirect: {location}");

        var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.StatusCode == HttpStatusCode.OK, $"Token exchange failed: {token.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        }));

    private static JsonElement DecodePayload(string jwt) =>
        JsonSerializer.Deserialize<JsonElement>(Base64UrlEncoder.DecodeBytes(jwt.Split('.')[1]));

    /// <summary>The "role" claim is a bare string for one role, an array for several.</summary>
    private static IReadOnlyList<string> GetRoles(JsonElement payload)
    {
        if (!payload.TryGetProperty("role", out var role))
            return Array.Empty<string>();
        return role.ValueKind == JsonValueKind.Array
            ? role.EnumerateArray().Select(r => r.GetString()!).ToList()
            : new[] { role.GetString()! };
    }

    private static async Task RemoveUserRoleAsync(RoleRecheckFactory factory, Guid roleId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var assignment = await db.UserRoles.SingleAsync(ur => ur.UserId == UserId && ur.RoleId == roleId);
        db.UserRoles.Remove(assignment);
        await db.SaveChangesAsync();
    }

    // ----- tests -----------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshedAccessToken_DoesNotCarryARoleRevokedAfterLogin()
    {
        using var factory = new RoleRecheckFactory();
        await SeedAsync(factory);
        var client = NewClient(factory);
        var tokens = await LoginAndGetTokensAsync(client);

        // Sanity: the login-issued token carries both roles.
        var loginRoles = GetRoles(DecodePayload(tokens.GetProperty("access_token").GetString()!));
        Assert.Contains(AdminRoleName, loginRoles);
        Assert.Contains(CustomerRoleName, loginRoles);

        // Revoke admin AFTER login, while the refresh token is still valid.
        await RemoveUserRoleAsync(factory, AdminRoleId);

        var refreshed = await RefreshAsync(NewClient(factory), tokens.GetProperty("refresh_token").GetString()!);
        var body = await refreshed.Content.ReadAsStringAsync();
        Assert.True(refreshed.StatusCode == HttpStatusCode.OK, $"refresh failed: {refreshed.StatusCode} {body}");

        var payload = DecodePayload(JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()!);
        var refreshedRoles = GetRoles(payload);
        Assert.DoesNotContain(AdminRoleName, refreshedRoles); // the revoked role is gone ...
        Assert.Contains(CustomerRoleName, refreshedRoles);    // ... the remaining one survives
    }

    [Fact]
    public async Task RefreshGrant_ForUserWhoLostEveryAppRole_IsRejected()
    {
        using var factory = new RoleRecheckFactory();
        await SeedAsync(factory);
        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        // Revoke BOTH taskmanager roles: the app-role gate that blocks login must now block refresh too.
        await RemoveUserRoleAsync(factory, AdminRoleId);
        await RemoveUserRoleAsync(factory, CustomerRoleId);

        var refreshed = await RefreshAsync(NewClient(factory), tokens.GetProperty("refresh_token").GetString()!);
        var body = await refreshed.Content.ReadAsStringAsync();
        Assert.True(refreshed.StatusCode == HttpStatusCode.BadRequest || refreshed.StatusCode == HttpStatusCode.Forbidden,
            $"a refresh token for an app the user lost access to was honoured: {refreshed.StatusCode} {body}");
        Assert.Contains(Errors.InvalidGrant, body);
    }

    [Fact]
    public async Task RefreshedAccessToken_RebuildsTenantIdFromDatabase_NotFromTheRefreshToken()
    {
        using var factory = new RoleRecheckFactory();
        await SeedAsync(factory);
        var client = NewClient(factory);
        var tokens = await LoginAndGetTokensAsync(client);

        // Sanity: login token is scoped to the seeded tenant.
        Assert.Equal(TenantId.ToString(),
            DecodePayload(tokens.GetProperty("access_token").GetString()!).GetProperty("tenant_id").GetString());

        // Un-scope the remaining assignment: drop admin, make customer global (TenantId = null).
        await RemoveUserRoleAsync(factory, AdminRoleId);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var assignment = await db.UserRoles.SingleAsync(ur => ur.UserId == UserId && ur.RoleId == CustomerRoleId);
            assignment.TenantId = null;
            await db.SaveChangesAsync();
        }

        var refreshed = await RefreshAsync(NewClient(factory), tokens.GetProperty("refresh_token").GetString()!);
        var body = await refreshed.Content.ReadAsStringAsync();
        Assert.True(refreshed.StatusCode == HttpStatusCode.OK, $"refresh failed: {refreshed.StatusCode} {body}");

        var payload = DecodePayload(JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()!);
        Assert.False(payload.TryGetProperty("tenant_id", out var stale),
            $"tenant_id was copied from the refresh token instead of rebuilt from the database: {stale}");
    }
}
