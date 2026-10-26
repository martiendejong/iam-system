using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.API.Workers;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5804: what the WordPress SSO plugin relies on, driven through the REAL pipeline in-process
/// (login -> /connect/authorize -> /connect/token -> /connect/logout) with the client registered by
/// <see cref="WordPressSsoSeeder"/> itself:
///   1. the discovery document advertises an end_session_endpoint (and the JWKS);
///   2. the id_token carries email_verified as a JSON boolean, and validates against the published JWKS
///      with exactly the issuer and audience a relying party derives from discovery and its client id;
///   3. the end-session endpoint ends the IAM session, honours a registered post_logout_redirect_uri
///      (echoing state) and refuses unregistered addresses and clients without the permission.
/// </summary>
public class WordPressSsoTests : IClassFixture<WordPressSsoTests.Factory>
{
    private const string ClientId = WordPressSsoSeeder.TestSiteClientId;
    private const string ClientSecret = "test-only-wordpress-secret-5804";
    private const string RedirectUri = "https://test.prospergenics.com/wp-login.php?action=jengo-iam-callback";
    private const string PostLogoutUri = "https://test.prospergenics.com/";
    private const string Password = "Str0ngTestPassw0rd!5804";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key-5804" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key-5804" };

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

    public WordPressSsoTests(Factory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------------

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private sealed record Seeded(string Email, Guid UserId);

    /// <summary>Registers the WordPress client + role catalog like startup does, and an active user holding the editor role.</summary>
    private async Task<Seeded> SeedAsync(bool emailConfirmed = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [WordPressSsoSeeder.TestSiteSecretConfigKey] = ClientSecret })
            .Build();
        var sites = WordPressSsoSeeder.ResolveSites(configuration, NullLogger.Instance);

        await WordPressSsoSeeder.SeedClientsAsync(
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(), sites, NullLogger.Instance, CancellationToken.None);
        await WordPressSsoSeeder.SeedAppRolesAsync(db, sites, NullLogger.Instance, CancellationToken.None);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var email = $"wp-{suffix}@wordpress-sso.test";
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId, Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "Word", LastName = "Press", EmailConfirmed = emailConfirmed, IsActive = true
        });
        var editor = await db.Roles.FirstAsync(r => r.Name == $"{ClientId}:editor");
        db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = editor.Id, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        return new Seeded(email, userId);
    }

    private static async Task LoginAsync(HttpClient client, Seeded user)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password });
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"Login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");
    }

    private async Task SetEmailConfirmedAsync(Guid userId, bool confirmed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        (await db.Users.FirstAsync(u => u.Id == userId)).EmailConfirmed = confirmed;
        await db.SaveChangesAsync();
    }

    private sealed record Pkce(string Verifier, string Challenge)
    {
        public static Pkce New()
        {
            var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            return new Pkce(verifier, Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }
    }

    private static string AuthorizeUrl(Pkce pkce, string nonce, string state = "state-5804", string promptNone = "") =>
        "/connect/authorize" +
        $"?client_id={Uri.EscapeDataString(ClientId)}" +
        "&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        "&scope=" + Uri.EscapeDataString("openid profile email roles") +
        $"&state={state}&nonce={nonce}" +
        $"&code_challenge={pkce.Challenge}&code_challenge_method=S256" + promptNone;

    /// <summary>The whole WordPress round trip: authorize (with the IAM session) and exchange the code like the plugin does.</summary>
    private async Task<string> GetIdTokenAsync(HttpClient client, string nonce)
    {
        var pkce = Pkce.New();
        var authorize = await client.GetAsync(AuthorizeUrl(pkce, nonce));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), $"No code in {authorize.Headers.Location}");

        var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["code_verifier"] = pkce.Verifier
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.StatusCode == HttpStatusCode.OK, $"Token exchange failed: {token.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id_token").GetString()!;
    }

    private static JsonElement PayloadOf(string jwt) =>
        JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1])).RootElement;

    private async Task<bool> SessionIsAliveAsync(HttpClient client)
    {
        var response = await client.GetAsync(AuthorizeUrl(Pkce.New(), "n", promptNone: "&prompt=none"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        return query.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code.ToString()) && !query.ContainsKey("error");
    }

    // ----- discovery ---------------------------------------------------------------------------------

    [Fact]
    public async Task Discovery_AdvertisesTheEndSessionEndpointAndTheJwks()
    {
        var response = await NewClient().GetAsync("/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.EndsWith("/connect/logout", doc.GetProperty("end_session_endpoint").GetString());
        Assert.StartsWith("https://", doc.GetProperty("end_session_endpoint").GetString());
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("jwks_uri").GetString()));
        Assert.Equal("S256", Assert.Single(doc.GetProperty("code_challenge_methods_supported").EnumerateArray()).GetString());
    }

    // ----- id_token contract with the plugin ----------------------------------------------------------

    [Fact]
    public async Task IdToken_CarriesEmailVerifiedTrueAsAJsonBoolean_ForAConfirmedAddress()
    {
        var user = await SeedAsync(emailConfirmed: true);
        var client = NewClient();
        await LoginAsync(client, user);

        var payload = PayloadOf(await GetIdTokenAsync(client, "nonce-a"));

        Assert.Equal(JsonValueKind.True, payload.GetProperty("email_verified").ValueKind);
        Assert.Equal(user.Email, payload.GetProperty("email").GetString());
    }

    [Fact]
    public async Task IdToken_CarriesEmailVerifiedFalse_ForAnUnconfirmedAddress()
    {
        var user = await SeedAsync(emailConfirmed: true);
        var client = NewClient();
        await LoginAsync(client, user);
        await SetEmailConfirmedAsync(user.UserId, false);

        var payload = PayloadOf(await GetIdTokenAsync(client, "nonce-b"));

        Assert.Equal(JsonValueKind.False, payload.GetProperty("email_verified").ValueKind);
    }

    [Fact]
    public async Task IdToken_ValidatesAgainstTheJwks_WithTheIssuerAndAudienceARelyingPartyDerives()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);
        var idToken = await GetIdTokenAsync(client, "nonce-c");

        // What the plugin does: issuer from the discovery document, keys from jwks_uri, audience = its client id.
        var discovery = JsonDocument.Parse(await (await client.GetAsync("/.well-known/openid-configuration")).Content.ReadAsStringAsync()).RootElement;
        var jwks = new JsonWebKeySet(await (await client.GetAsync("/.well-known/jwks")).Content.ReadAsStringAsync());
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = discovery.GetProperty("issuer").GetString(),
            ValidAudience = ClientId,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidateLifetime = true
        });

        Assert.True(result.IsValid, result.Exception?.Message);
        var payload = PayloadOf(idToken);
        Assert.Equal("nonce-c", payload.GetProperty("nonce").GetString());
        Assert.Equal(user.UserId.ToString(), payload.GetProperty("sub").GetString());
        Assert.Contains($"{ClientId}:editor", RolesOf(payload));
        Assert.Equal("RS256", JsonDocument.Parse(Base64UrlEncoder.Decode(idToken.Split('.')[0])).RootElement.GetProperty("alg").GetString());
    }

    private static string[] RolesOf(JsonElement payload) =>
        payload.GetProperty("role").ValueKind == JsonValueKind.Array
            ? payload.GetProperty("role").EnumerateArray().Select(e => e.GetString()!).ToArray()
            : new[] { payload.GetProperty("role").GetString()! };

    // ----- end session ----------------------------------------------------------------------------------

    [Fact]
    public async Task EndSession_EndsTheIamSession_AndRedirectsToTheRegisteredPostLogoutUri_EchoingState()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);
        Assert.True(await SessionIsAliveAsync(client));

        var response = await client.GetAsync(
            $"/connect/logout?client_id={ClientId}&post_logout_redirect_uri={Uri.EscapeDataString(PostLogoutUri)}&state=bye-5804");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(PostLogoutUri, location.ToString());
        Assert.Equal("bye-5804", QueryHelpers.ParseQuery(location.Query)["state"].ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c =>
            c.StartsWith("IAM.Session=", StringComparison.Ordinal) && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        // The session is really gone: a silent sign-in now says login_required instead of issuing a code.
        var silent = await client.GetAsync(AuthorizeUrl(Pkce.New(), "n", promptNone: "&prompt=none"));
        Assert.Equal(Errors.LoginRequired, QueryHelpers.ParseQuery(silent.Headers.Location!.Query)["error"].ToString());
    }

    [Fact]
    public async Task EndSession_AlsoAcceptsAFormPost()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);

        var response = await client.PostAsync("/connect/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["post_logout_redirect_uri"] = PostLogoutUri
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(PostLogoutUri, response.Headers.Location!.ToString());
        Assert.False(await SessionIsAliveAsync(client));
    }

    [Fact]
    public async Task EndSession_WithoutAPostLogoutRedirectUri_StillEndsTheSession_AndLandsOnTheIamLoginPage()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);

        var response = await client.GetAsync($"/connect/logout?client_id={ClientId}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.ToString());
        Assert.False(await SessionIsAliveAsync(client));
    }

    [Fact]
    public async Task EndSession_NeverRedirectsToAnUnregisteredAddress()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);
        const string evil = "https://evil.example.test/phish";

        var response = await client.GetAsync(
            $"/connect/logout?client_id={ClientId}&post_logout_redirect_uri={Uri.EscapeDataString(evil)}&state=x");

        Assert.False(response.Headers.Location?.ToString().StartsWith(evil, StringComparison.Ordinal) ?? false,
            $"Redirected to an unregistered post_logout_redirect_uri: {response.Headers.Location}");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EndSession_ForAClientWithoutTheEndSessionPermission_IsRefused_AndTheSessionSurvives()
    {
        var user = await SeedAsync();
        var client = NewClient();
        await LoginAsync(client, user);

        const string otherClient = "wp-no-endsession-5804";
        using (var scope = _factory.Services.CreateScope())
        {
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            if (await apps.FindByClientIdAsync(otherClient) == null)
            {
                await apps.CreateAsync(new OpenIddictApplicationDescriptor
                {
                    ClientId = otherClient,
                    ClientType = ClientTypes.Public,
                    ConsentType = ConsentTypes.Implicit,
                    DisplayName = "Client without end_session permission",
                    RedirectUris = { new Uri("https://nologout.example.test/cb") },
                    PostLogoutRedirectUris = { new Uri("https://nologout.example.test/") },
                    Permissions =
                    {
                        Permissions.Endpoints.Authorization,
                        Permissions.Endpoints.Token,
                        Permissions.GrantTypes.AuthorizationCode,
                        Permissions.ResponseTypes.Code
                    }
                });
            }
        }

        var response = await client.GetAsync(
            $"/connect/logout?client_id={otherClient}&post_logout_redirect_uri={Uri.EscapeDataString("https://nologout.example.test/")}");

        Assert.False(response.Headers.Location?.ToString().StartsWith("https://nologout.example.test/", StringComparison.Ordinal) ?? false);
        Assert.True(await SessionIsAliveAsync(client), "A refused logout request must not end the session");
    }
}
