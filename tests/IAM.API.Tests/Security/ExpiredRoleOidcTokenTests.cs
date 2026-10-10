using System.Net;
using System.Net.Http.Headers;
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
/// Task 5156: a time-boxed role (UserRole.ExpiresAt) must stop being issued as a role claim in the OIDC
/// access token, the ID token and the userinfo response, and an expired app role must stop counting for the
/// app sign-in check and for the tenant the token is scoped to. Same in-process harness as
/// RefreshTokenRoleRecheckTests (real login -> /connect/authorize -> /connect/token -> refresh, in-memory
/// RSA keys); the role expiry is changed in the database between the steps, the way time passing would.
/// </summary>
public class ExpiredRoleOidcTokenTests
{
    private const string ClientId = "taskmanager";
    private const string RedirectUri = "https://taskmanager.example.test/callback";
    private const string UserEmail = "expired-role@test.example";
    private const string UserPassword = "Str0ngTestPassw0rd!5156";
    private const string CustomerRoleName = "taskmanager:customer";
    private const string AdminRoleName = "taskmanager:admin";
    private const string SuperAdminRoleName = "SuperAdmin";
    private const string AuditorRoleName = "Auditor";
    private static readonly Guid TenantA = Guid.Parse("33333333-5156-5156-5156-333333333331");
    private static readonly Guid TenantB = Guid.Parse("33333333-5156-5156-5156-333333333332");
    private static readonly Guid UserId = Guid.Parse("44444444-5156-5156-5156-444444444444");
    private static readonly Guid CustomerRoleId = Guid.Parse("55555555-5156-5156-5156-555555555551");
    private static readonly Guid AdminRoleId = Guid.Parse("55555555-5156-5156-5156-555555555552");
    private static readonly Guid SuperAdminRoleId = Guid.Parse("55555555-5156-5156-5156-555555555553");
    private static readonly Guid AuditorRoleId = Guid.Parse("55555555-5156-5156-5156-555555555554");

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key-5156" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key-5156" };

    private sealed class ExpiredRoleFactory : IAMTestWebApplicationFactory
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

    private static HttpClient NewClient(ExpiredRoleFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    /// <summary>One assignment to seed: which role, which tenant, and when (if ever) it lapses.</summary>
    private sealed record Grant(Guid RoleId, Guid? TenantId, DateTime? ExpiresAt);

    /// <summary>
    /// Seeds the taskmanager client WITH a role catalog (app-role gate active), two tenants, two app roles, a
    /// global SuperAdmin and a global Auditor role, and the user holding exactly <paramref name="grants"/>.
    /// </summary>
    private static async Task SeedAsync(ExpiredRoleFactory factory, params Grant[] grants)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        db.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Tenant A", Type = "Organization", IsActive = true },
            new Tenant { Id = TenantB, Name = "Tenant B", Type = "Organization", IsActive = true });

        db.Roles.AddRange(
            new Role { Id = CustomerRoleId, Name = CustomerRoleName, Category = $"app:{ClientId}", TenantId = null, IsSystemRole = false },
            new Role { Id = AdminRoleId, Name = AdminRoleName, Category = $"app:{ClientId}", TenantId = null, IsSystemRole = false },
            new Role { Id = SuperAdminRoleId, Name = SuperAdminRoleName, TenantId = null, IsSystemRole = true },
            new Role { Id = AuditorRoleId, Name = AuditorRoleName, TenantId = null, IsSystemRole = false });

        db.Users.Add(new User
        {
            Id = UserId,
            Email = UserEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(UserPassword),
            FirstName = "Expired",
            LastName = "Role",
            EmailConfirmed = true,
            IsActive = true
        });

        foreach (var grant in grants)
        {
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                RoleId = grant.RoleId,
                TenantId = grant.TenantId,
                GrantedAt = DateTime.UtcNow,
                ExpiresAt = grant.ExpiresAt
            });
        }
        await db.SaveChangesAsync();

        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "TaskManager (expired role test client)",
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
                $"{Permissions.Prefixes.Scope}tenants",
                $"{Permissions.Prefixes.Scope}{Scopes.OfflineAccess}"
            }
        });
    }

    private static async Task SetExpiryAsync(ExpiredRoleFactory factory, Guid roleId, Guid? tenantId, DateTime? expiresAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var assignment = await db.UserRoles.SingleAsync(ur => ur.UserId == UserId && ur.RoleId == roleId && ur.TenantId == tenantId);
        assignment.ExpiresAt = expiresAt;
        await db.SaveChangesAsync();
    }

    private static DateTime Past => DateTime.UtcNow.AddMinutes(-1);
    private static DateTime Future => DateTime.UtcNow.AddDays(30);

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = UserEmail, password = UserPassword });
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"Login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");
    }

    private static Task<HttpResponseMessage> AuthorizeAsync(HttpClient client) =>
        client.GetAsync("/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(ClientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=" + Uri.EscapeDataString("openid profile email roles tenants offline_access") +
            "&state=xyz");

    private static async Task<string> GetCodeAsync(HttpClient client)
    {
        var authorize = await AuthorizeAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var location = authorize.Headers.Location!;
        Assert.StartsWith(RedirectUri, location.ToString());
        var code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), $"No authorization code in redirect: {location}");
        return code;
    }

    private static async Task<JsonElement> ExchangeCodeAsync(HttpClient client, string code)
    {
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

    /// <summary>Real browser-equivalent flow: login, authorize (cookie), exchange the code.</summary>
    private static async Task<JsonElement> LoginAndGetTokensAsync(HttpClient client)
    {
        await LoginAsync(client);
        return await ExchangeCodeAsync(client, await GetCodeAsync(client));
    }

    private static async Task<JsonElement> RefreshOkAsync(HttpClient client, JsonElement tokens)
    {
        var refreshed = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = ClientId
        }));
        var body = await refreshed.Content.ReadAsStringAsync();
        Assert.True(refreshed.StatusCode == HttpStatusCode.OK, $"refresh failed: {refreshed.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static JsonElement DecodePayload(string jwt) =>
        JsonSerializer.Deserialize<JsonElement>(Base64UrlEncoder.DecodeBytes(jwt.Split('.')[1]));

    /// <summary>The "role" claim is a bare string for one role, an array for several, absent for none.</summary>
    private static IReadOnlyList<string> RolesIn(JsonElement payload)
    {
        if (!payload.TryGetProperty("role", out var role))
            return Array.Empty<string>();
        var roles = role.ValueKind == JsonValueKind.Array
            ? role.EnumerateArray().Select(r => r.GetString()!)
            : new[] { role.GetString()! };
        return roles.OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<string> AccessTokenRoles(JsonElement tokenResponse) =>
        RolesIn(DecodePayload(tokenResponse.GetProperty("access_token").GetString()!));

    private static IReadOnlyList<string> IdTokenRoles(JsonElement tokenResponse) =>
        RolesIn(DecodePayload(tokenResponse.GetProperty("id_token").GetString()!));

    private static string? TenantIdIn(JsonElement tokenResponse)
    {
        var payload = DecodePayload(tokenResponse.GetProperty("access_token").GetString()!);
        return payload.TryGetProperty("tenant_id", out var tenant) ? tenant.GetString() : null;
    }

    private static async Task<JsonElement> UserinfoAsync(ExpiredRoleFactory factory, JsonElement tokens)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        var response = await NewClient(factory).SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"userinfo failed: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    // ----- initial grant (authorize + code exchange) ------------------------------------------------

    [Fact]
    public async Task Login_AccessAndIdToken_CarryOnlyRolesWithNoExpiryOrAFutureExpiry()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory,
            new Grant(CustomerRoleId, TenantA, null),        // no expiry: issued as before
            new Grant(AuditorRoleId, null, Future),          // future expiry: issued as before
            new Grant(AdminRoleId, TenantA, Past),           // lapsed app admin role
            new Grant(SuperAdminRoleId, null, Past));        // lapsed temporary SuperAdmin

        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        var expected = new[] { AuditorRoleName, CustomerRoleName };
        Assert.Equal(expected, AccessTokenRoles(tokens));
        Assert.Equal(expected, IdTokenRoles(tokens));
    }

    [Fact]
    public async Task Login_TheTenantOfAnExpiredAppRole_IsNotTakenIntoTheToken()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory,
            new Grant(AdminRoleId, TenantA, Past),           // expired, would be picked first on the old code
            new Grant(CustomerRoleId, TenantB, null));

        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        Assert.Equal(TenantB.ToString(), TenantIdIn(tokens));
    }

    [Fact]
    public async Task Authorize_UserWhoseOnlyAppRoleExpired_IsDeniedAccessToTheApp()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory,
            new Grant(AdminRoleId, TenantA, Past),
            new Grant(AuditorRoleId, null, null));           // a non-app role does not open the app

        var client = NewClient(factory);
        await LoginAsync(client);
        var authorize = await AuthorizeAsync(client);

        Assert.Equal(HttpStatusCode.OK, authorize.StatusCode);
        Assert.Contains("No access to this application", await authorize.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorize_UserWhoseAppRoleStillRuns_IsLetIn()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory, new Grant(AdminRoleId, TenantA, Future));

        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        Assert.Equal(new[] { AdminRoleName }, AccessTokenRoles(tokens));
        Assert.Equal(TenantA.ToString(), TenantIdIn(tokens));
    }

    [Fact]
    public async Task CodeExchange_RoleThatLapsedAfterAuthorize_IsNotCopiedIntoTheTokens()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory,
            new Grant(CustomerRoleId, TenantA, null),
            new Grant(SuperAdminRoleId, null, Future));
        var client = NewClient(factory);
        await LoginAsync(client);
        var code = await GetCodeAsync(client);               // the code is minted while SuperAdmin is still valid

        await SetExpiryAsync(factory, SuperAdminRoleId, null, Past);
        var tokens = await ExchangeCodeAsync(client, code);

        Assert.Equal(new[] { CustomerRoleName }, AccessTokenRoles(tokens));
        Assert.Equal(new[] { CustomerRoleName }, IdTokenRoles(tokens));
    }

    // ----- refresh grant ----------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_RoleThatLapsedAfterLogin_IsGoneFromTheRefreshedAccessToken()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory,
            new Grant(CustomerRoleId, TenantA, null),
            new Grant(AdminRoleId, TenantA, DateTime.UtcNow.AddHours(1)),
            new Grant(SuperAdminRoleId, null, DateTime.UtcNow.AddHours(1)));
        var tokens = await LoginAndGetTokensAsync(NewClient(factory));
        Assert.Equal(new[] { SuperAdminRoleName, AdminRoleName, CustomerRoleName }, AccessTokenRoles(tokens)); // valid at login (ordinal order)

        await SetExpiryAsync(factory, AdminRoleId, TenantA, Past);
        await SetExpiryAsync(factory, SuperAdminRoleId, null, Past);
        var refreshed = await RefreshOkAsync(NewClient(factory), tokens);

        Assert.Equal(new[] { CustomerRoleName }, AccessTokenRoles(refreshed));
        Assert.Equal(new[] { CustomerRoleName }, IdTokenRoles(refreshed));
    }

    [Fact]
    public async Task Refresh_ExpiredAppRoleDoesNotKeepItsTenant_TheTokenMovesToTheRoleThatStillRuns()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory,
            new Grant(AdminRoleId, TenantA, DateTime.UtcNow.AddHours(1)),
            new Grant(CustomerRoleId, TenantB, null));
        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        await SetExpiryAsync(factory, AdminRoleId, TenantA, Past);
        var refreshed = await RefreshOkAsync(NewClient(factory), tokens);

        Assert.Equal(TenantB.ToString(), TenantIdIn(refreshed));
        Assert.Equal(new[] { CustomerRoleName }, AccessTokenRoles(refreshed));
    }

    [Fact]
    public async Task Refresh_WhenEveryAppRoleHasLapsed_IsRejectedLikeARevokedRole()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory, new Grant(AdminRoleId, TenantA, DateTime.UtcNow.AddHours(1)));
        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        await SetExpiryAsync(factory, AdminRoleId, TenantA, Past);
        var refreshed = await NewClient(factory).PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = ClientId
        }));

        var body = await refreshed.Content.ReadAsStringAsync();
        Assert.True(refreshed.StatusCode == HttpStatusCode.BadRequest || refreshed.StatusCode == HttpStatusCode.Forbidden,
            $"a refresh token for an app whose role has lapsed was honoured: {refreshed.StatusCode} {body}");
        Assert.Contains(Errors.InvalidGrant, body);
    }

    [Fact]
    public async Task Refresh_RoleWithNoExpiryOrAFutureExpiry_IsIssuedExactlyAsBefore()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory,
            new Grant(CustomerRoleId, TenantA, null),
            new Grant(AuditorRoleId, null, Future));
        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        var refreshed = await RefreshOkAsync(NewClient(factory), tokens);

        Assert.Equal(new[] { AuditorRoleName, CustomerRoleName }, AccessTokenRoles(refreshed));
        Assert.Equal(TenantA.ToString(), TenantIdIn(refreshed));
    }

    // ----- userinfo ---------------------------------------------------------------------------------

    [Fact]
    public async Task Userinfo_ListsOnlyActiveRolesAndOnlyTenantsWithAnActiveRole()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory,
            new Grant(CustomerRoleId, TenantA, null),
            new Grant(AdminRoleId, TenantB, DateTime.UtcNow.AddHours(1)),      // tenant B only through this role
            new Grant(SuperAdminRoleId, null, DateTime.UtcNow.AddHours(1)));
        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        var before = await UserinfoAsync(factory, tokens);
        Assert.Equal(2, before.GetProperty("tenants").GetArrayLength());       // sanity: both tenants while valid

        await SetExpiryAsync(factory, AdminRoleId, TenantB, Past);
        await SetExpiryAsync(factory, SuperAdminRoleId, null, Past);
        var after = await UserinfoAsync(factory, tokens);

        Assert.Equal(new[] { CustomerRoleName }, RolesIn(after));
        var tenants = after.GetProperty("tenants").EnumerateArray().ToList();
        var only = Assert.Single(tenants);
        Assert.Equal(TenantA.ToString(), only.GetProperty("id").GetString());
        Assert.Equal(CustomerRoleName, only.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Userinfo_UserWithOnlyLapsedRoles_GetsNoRoleClaimAndNoTenants()
    {
        using var factory = new ExpiredRoleFactory();
        await SeedAsync(factory, new Grant(CustomerRoleId, TenantA, DateTime.UtcNow.AddHours(1)));
        var tokens = await LoginAndGetTokensAsync(NewClient(factory));

        await SetExpiryAsync(factory, CustomerRoleId, TenantA, Past);
        var info = await UserinfoAsync(factory, tokens);

        Assert.Empty(RolesIn(info));
        Assert.Equal(0, info.GetProperty("tenants").GetArrayLength());
    }
}
