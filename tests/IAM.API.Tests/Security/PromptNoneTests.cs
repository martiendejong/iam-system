using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
/// Task 5053: an app (Jengo Knowledge) must be able to renew its IAM sign-in silently. These tests drive the
/// REAL pipeline in-process (login -> /connect/authorize), same in-memory-RSA harness as SuperAdminOfflineAccessTests:
///   1. prompt=none never answers with IAM UI - no login redirect, no HTML "No access" page, no 400 - but with an
///      OAuth error redirect to the app's redirect_uri (login_required / access_denied) carrying the original state;
///      without prompt=none the interactive behaviour is unchanged.
///   2. The IAM.Session cookie is SameSite=Lax so a cross-site redirect from an app carries it; the refreshToken
///      cookie stays Strict.
/// </summary>
public class PromptNoneTests : IClassFixture<PromptNoneTests.Factory>
{
    private const string RedirectUri = "https://knowledge.example.test/callback";
    private const string Password = "Str0ngTestPassw0rd!5053";
    private const string State = "state-5053-abc";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key-5053" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key-5053" };

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

    public PromptNoneTests(Factory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------------

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private sealed record Seeded(string ClientId, string Email, Guid UserId);

    /// <summary>
    /// Seeds a PKCE-requiring public client (like jengo-knowledge) and an active user. With
    /// <paramref name="appRoleCatalog"/> the client gets an "app:{client}" role catalog, which turns the
    /// federated app-role gate on; the user holds that app's role only when <paramref name="userHoldsAppRole"/>.
    /// </summary>
    private async Task<Seeded> SeedAsync(bool appRoleCatalog = false, bool userHoldsAppRole = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var clientId = $"app{suffix}";
        var email = $"user-{suffix}@prompt-none.test";
        var userId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        db.Users.Add(new User
        {
            Id = userId, Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "Prompt", LastName = "None", EmailConfirmed = true, IsActive = true
        });

        if (appRoleCatalog)
        {
            var role = new Role
            {
                Id = Guid.NewGuid(), Name = $"{clientId}:member", TenantId = null, IsSystemRole = false,
                Category = $"app:{clientId}"
            };
            db.Roles.Add(role);
            if (userHoldsAppRole)
                db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, GrantedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();

        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Prompt none test client",
            RedirectUris = { new Uri(RedirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                $"{Permissions.Prefixes.Scope}{Scopes.OpenId}",
                $"{Permissions.Prefixes.Scope}{Scopes.Profile}",
                $"{Permissions.Prefixes.Scope}{Scopes.Email}"
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        });

        return new Seeded(clientId, email, userId);
    }

    private static string AuthorizeUrl(string clientId, bool promptNone, string redirectUri = RedirectUri)
    {
        // Real S256 challenge, like the jengo-knowledge client sends.
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        return "/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&scope=" + Uri.EscapeDataString("openid profile email") +
            $"&state={State}" +
            $"&code_challenge={challenge}&code_challenge_method=S256" +
            (promptNone ? "&prompt=none" : string.Empty);
    }

    private static async Task LoginAsync(HttpClient client, Seeded user)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password });
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"Login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");
    }

    /// <summary>Asserts a 302 to the app's redirect_uri carrying error + state and no code; returns the query.</summary>
    private static void AssertErrorRedirectToApp(HttpResponseMessage response, string expectedError)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        Assert.StartsWith(RedirectUri, location!.ToString());

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(expectedError, query["error"].ToString());
        Assert.Equal(State, query["state"].ToString());
        Assert.False(query.ContainsKey("code"), $"No code may be issued with an error: {location}");
    }

    // ----- prompt=none: no session ------------------------------------------------------------------

    [Fact]
    public async Task PromptNone_WithoutSession_RedirectsToAppWithLoginRequiredAndState()
    {
        var user = await SeedAsync();

        var response = await NewClient().GetAsync(AuthorizeUrl(user.ClientId, promptNone: true));

        AssertErrorRedirectToApp(response, Errors.LoginRequired);
        Assert.DoesNotContain("/auth/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task PromptNone_WithoutSession_PostForm_RedirectsToAppWithLoginRequiredAndState()
    {
        var user = await SeedAsync();
        var uri = new Uri("https://localhost" + AuthorizeUrl(user.ClientId, promptNone: true));
        var form = QueryHelpers.ParseQuery(uri.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

        var response = await NewClient().PostAsync("/connect/authorize", new FormUrlEncodedContent(form));

        AssertErrorRedirectToApp(response, Errors.LoginRequired);
    }

    [Fact]
    public async Task WithoutPromptNone_WithoutSession_StillRedirectsToTheLoginPage()
    {
        var user = await SeedAsync();

        var response = await NewClient().GetAsync(AuthorizeUrl(user.ClientId, promptNone: false));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("/auth/login?returnUrl=", location);
        Assert.DoesNotContain(RedirectUri, location.Replace(Uri.EscapeDataString(RedirectUri), string.Empty));
    }

    [Fact]
    public async Task PromptNone_WithAnUnregisteredRedirectUri_NeverRedirectsToThatUri()
    {
        var user = await SeedAsync();
        const string evil = "https://evil.example.test/callback";

        var response = await NewClient().GetAsync(AuthorizeUrl(user.ClientId, promptNone: true, redirectUri: evil));

        // OpenIddict rejects the request before the controller runs and must not bounce the browser to an
        // unregistered address, whatever prompt says.
        Assert.False(response.Headers.Location?.ToString().StartsWith(evil) ?? false,
            $"Redirected to an unregistered redirect_uri: {response.Headers.Location}");
    }

    // ----- prompt=none: session present -------------------------------------------------------------

    [Fact]
    public async Task PromptNone_WithSession_SignsInSilentlyWithoutIamUi()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);

        var response = await client.GetAsync(AuthorizeUrl(user.ClientId, promptNone: true));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(RedirectUri, location.ToString());
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.False(string.IsNullOrEmpty(query["code"].ToString()), $"No authorization code in redirect: {location}");
        Assert.Equal(State, query["state"].ToString());
        Assert.False(query.ContainsKey("error"));
    }

    [Fact]
    public async Task PromptNone_InactiveUser_RedirectsToAppWithLoginRequired()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);
        await SetActiveAsync(user.UserId, false);

        var response = await client.GetAsync(AuthorizeUrl(user.ClientId, promptNone: true));

        AssertErrorRedirectToApp(response, Errors.LoginRequired);
    }

    [Fact]
    public async Task WithoutPromptNone_InactiveUser_StillGetsTheBadRequest()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);
        await SetActiveAsync(user.UserId, false);

        var response = await client.GetAsync(AuthorizeUrl(user.ClientId, promptNone: false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PromptNone_UserWithoutAppRole_RedirectsToAppWithAccessDenied()
    {
        var user = await SeedAsync(appRoleCatalog: true, userHoldsAppRole: false);
        var client = NewClient();
        await LoginAsync(client, user);

        var response = await client.GetAsync(AuthorizeUrl(user.ClientId, promptNone: true));

        AssertErrorRedirectToApp(response, Errors.AccessDenied);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WithoutPromptNone_UserWithoutAppRole_StillGetsTheNoAccessPage()
    {
        var user = await SeedAsync(appRoleCatalog: true, userHoldsAppRole: false);
        var client = NewClient();
        await LoginAsync(client, user);

        var response = await client.GetAsync(AuthorizeUrl(user.ClientId, promptNone: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("No access to this application", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PromptNone_UserWithAppRole_SignsInSilently()
    {
        var user = await SeedAsync(appRoleCatalog: true, userHoldsAppRole: true);
        var client = NewClient();
        await LoginAsync(client, user);

        var response = await client.GetAsync(AuthorizeUrl(user.ClientId, promptNone: true));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"].ToString()));
    }

    // ----- cookie attributes ------------------------------------------------------------------------

    [Fact]
    public async Task Login_SetsTheSessionCookieSameSiteLax_AndKeepsTheRefreshTokenCookieStrict()
    {
        var user = await SeedAsync();

        var login = await NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookies = login.Headers.GetValues("Set-Cookie").ToList();
        var session = Assert.Single(setCookies, c => c.StartsWith("IAM.Session=", StringComparison.Ordinal));
        var refresh = Assert.Single(setCookies, c => c.StartsWith("refreshToken=", StringComparison.Ordinal));

        Assert.Contains("samesite=lax", session, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("samesite=strict", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", refresh, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SetActiveAsync(Guid userId, bool isActive)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var entity = await db.Users.FirstAsync(u => u.Id == userId);
        entity.IsActive = isActive;
        await db.SaveChangesAsync();
    }
}
