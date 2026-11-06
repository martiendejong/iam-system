using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using IAM.API.Auth;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5490: the IAM.Session single sign-on cookie is tied to a per-user security stamp, has an absolute
/// lifetime, and /connect/authorize honours prompt=login and max_age. A cookie is copied after login and
/// replayed after each event; replaying must not get an authorization code any more.
/// </summary>
public class SsoSessionStampTests : IClassFixture<SsoSessionStampTests.Factory>
{
    private const string RedirectUri = "https://knowledge.example.test/callback";
    private const string Password = "Str0ngTestPassw0rd!5490";
    private const string NewPassword = "An0therStr0ngPassw0rd!5490";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key-5490" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key-5490" };

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

    public SsoSessionStampTests(Factory factory) => _factory = factory;

    private HttpClient NewClient(string? cookie = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        if (cookie != null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private sealed record Seeded(string ClientId, string Email, Guid UserId);

    private async Task<Seeded> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var clientId = $"app{suffix}";
        var email = $"user-{suffix}@sso-stamp.test";
        var userId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.Users.Add(new User
        {
            Id = userId, Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "Sso", LastName = "Stamp", EmailConfirmed = true, IsActive = true
        });
        await db.SaveChangesAsync();

        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "SSO stamp test client",
            RedirectUris = { new Uri(RedirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                $"{Permissions.Prefixes.Scope}{Scopes.OpenId}",
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        });
        return new Seeded(clientId, email, userId);
    }

    private static string AuthorizeUrl(string clientId, string extra = "")
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        return "/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=openid&state=s5490" +
            $"&code_challenge={challenge}&code_challenge_method=S256" + extra;
    }

    /// <summary>Logs in and returns the IAM.Session cookie header value plus the access token.</summary>
    private async Task<(string Cookie, string AccessToken)> LoginAsync(Seeded user)
    {
        var response = await NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("IAM.Session="));
        var cookie = setCookie.Split(';')[0];
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (cookie, json.RootElement.GetProperty("accessToken").GetString()!);
    }

    private static void AssertIssuesCode(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(RedirectUri, location.ToString());
        Assert.False(string.IsNullOrEmpty(QueryHelpers.ParseQuery(location.Query)["code"].ToString()), location.ToString());
    }

    private static void AssertSendsToLogin(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/auth/login?returnUrl=", response.Headers.Location!.ToString());
    }

    /// <summary>Builds a cookie exactly as login would, but for a chosen sign-in moment.</summary>
    private async Task<string> CraftCookieAsync(Seeded seeded, DateTimeOffset signedInAt, bool withStamp = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == seeded.UserId);
        var identity = SsoSession.BuildIdentity(user, rememberMe: false, signedInAt, SsoSession.DefaultAbsoluteHours);
        if (!withStamp)
        {
            foreach (var c in identity.FindAll(SsoSession.StampClaim).ToList()) identity.RemoveClaim(c);
        }
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SsoSession.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        }, SsoSession.Scheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    // ----- the stamp -------------------------------------------------------------------------------

    [Fact]
    public async Task FreshLogin_CookieGetsACode()
    {
        var user = await SeedAsync();
        var (cookie, _) = await LoginAsync(user);

        AssertIssuesCode(await NewClient(cookie).GetAsync(AuthorizeUrl(user.ClientId)));
    }

    [Fact]
    public async Task ApiLogout_CopiedCookieNoLongerGetsACode()
    {
        var user = await SeedAsync();
        var (cookie, _) = await LoginAsync(user);
        var copy = cookie;

        var logout = await NewClient(cookie).PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        AssertSendsToLogin(await NewClient(copy).GetAsync(AuthorizeUrl(user.ClientId)));
        var silent = await NewClient(copy).GetAsync(AuthorizeUrl(user.ClientId, "&prompt=none"));
        Assert.Contains("error=login_required", silent.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ConnectLogout_ClearsTheCookieAndTheCopyIsDead()
    {
        var user = await SeedAsync();
        var (cookie, _) = await LoginAsync(user);

        var logout = await NewClient(cookie).GetAsync("/connect/logout");
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith("IAM.Session=;"));

        AssertSendsToLogin(await NewClient(cookie).GetAsync(AuthorizeUrl(user.ClientId)));
    }

    [Fact]
    public async Task PortalPasswordChange_CopiedCookieNoLongerGetsACode()
    {
        var user = await SeedAsync();
        var (cookie, token) = await LoginAsync(user);

        var client = NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var change = await client.PostAsJsonAsync("/api/portal/change-password",
            new { currentPassword = Password, newPassword = NewPassword });
        Assert.True(change.IsSuccessStatusCode, await change.Content.ReadAsStringAsync());

        AssertSendsToLogin(await NewClient(cookie).GetAsync(AuthorizeUrl(user.ClientId)));
    }

    [Fact]
    public async Task Deactivation_CookieStaysDeadEvenAfterReactivation()
    {
        var user = await SeedAsync();
        var (cookie, _) = await LoginAsync(user);

        using (var scope = _factory.Services.CreateScope())
        {
            // Same steps as UsersController.DeactivateUser.
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var entity = await db.Users.FirstAsync(u => u.Id == user.UserId);
            entity.IsActive = false;
            await db.RevokeRefreshTokensAsync(user.UserId);
            await db.SaveChangesAsync();
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            (await db.Users.FirstAsync(u => u.Id == user.UserId)).IsActive = true;
            await db.SaveChangesAsync();
        }

        AssertSendsToLogin(await NewClient(cookie).GetAsync(AuthorizeUrl(user.ClientId)));
    }

    [Fact]
    public async Task LegacyCookieWithoutStamp_IsAskedToSignInAgain()
    {
        var user = await SeedAsync();
        var legacy = await CraftCookieAsync(user, DateTimeOffset.UtcNow, withStamp: false);

        AssertSendsToLogin(await NewClient(legacy).GetAsync(AuthorizeUrl(user.ClientId)));
    }

    // ----- absolute lifetime -----------------------------------------------------------------------

    [Fact]
    public async Task SessionPastItsAbsoluteLifetime_IsRejectedEvenWhenStillSliding()
    {
        var user = await SeedAsync();
        var old = await CraftCookieAsync(user, DateTimeOffset.UtcNow.AddHours(-(SsoSession.DefaultAbsoluteHours + 1)));
        var young = await CraftCookieAsync(user, DateTimeOffset.UtcNow.AddHours(-1));

        AssertSendsToLogin(await NewClient(old).GetAsync(AuthorizeUrl(user.ClientId)));
        AssertIssuesCode(await NewClient(young).GetAsync(AuthorizeUrl(user.ClientId)));
    }

    // ----- prompt=login and max_age ----------------------------------------------------------------

    [Fact]
    public async Task PromptLogin_ForcesSignInAndTheReturnUrlDropsIt()
    {
        var user = await SeedAsync();
        var (cookie, _) = await LoginAsync(user);

        var response = await NewClient(cookie).GetAsync(AuthorizeUrl(user.ClientId, "&prompt=login"));

        AssertSendsToLogin(response);
        var returnUrl = QueryHelpers.ParseQuery(response.Headers.Location!.OriginalString.Split('?', 2)[1])["returnUrl"].ToString();
        Assert.StartsWith("/connect/authorize", returnUrl);
        Assert.DoesNotContain("prompt=login", returnUrl); // else the login page would loop
        // After that sign-in the same request is satisfied.
        AssertIssuesCode(await NewClient(cookie).GetAsync(returnUrl));
    }

    [Fact]
    public async Task PromptNoneTogetherWithMaxAgeTooOld_AnswersLoginRequiredWithoutUi()
    {
        var user = await SeedAsync();
        var cookie = await CraftCookieAsync(user, DateTimeOffset.UtcNow.AddHours(-2));

        var response = await NewClient(cookie).GetAsync(AuthorizeUrl(user.ClientId, "&prompt=none&max_age=60"));

        Assert.StartsWith(RedirectUri, response.Headers.Location!.ToString());
        Assert.Contains("error=login_required", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task MaxAge_ForcesSignInOnlyWhenTheLastSignInIsOlder()
    {
        var user = await SeedAsync();
        var cookie = await CraftCookieAsync(user, DateTimeOffset.UtcNow.AddHours(-2));

        AssertSendsToLogin(await NewClient(cookie).GetAsync(AuthorizeUrl(user.ClientId, "&max_age=3600")));
        AssertIssuesCode(await NewClient(cookie).GetAsync(AuthorizeUrl(user.ClientId, "&max_age=86400")));
    }
}
