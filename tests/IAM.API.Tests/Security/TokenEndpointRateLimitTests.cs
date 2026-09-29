using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 4097: failed client authentication at /connect/token must be rate limited per source IP.
///
/// OpenIddict rejects invalid token requests inside UseAuthentication, BEFORE the general
/// RateLimitingMiddleware, so before this task a wrong-secret flood was never throttled (a probe of
/// 200 wrong-secret requests never saw a 429). These tests drive the REAL pipeline in-process:
/// TokenEndpointRateLimitingMiddleware -> UseAuthentication (OpenIddict) -> controller.
///
/// The source IP is injected per request via the X-Test-Client-IP header, translated into
/// HttpContext.Connection.RemoteIpAddress by a test-only IStartupFilter that runs ahead of the app
/// pipeline (the TestServer connection has no remote IP of its own). This emulates what
/// UseForwardedHeaders does in production; the production middleware itself never reads a header.
///
/// The OpenIddict signing/encryption keys are swapped for in-memory RSA keys because the factory
/// runs in Development, where AddDevelopment*Certificate() hits a CNG "Keyset does not exist"
/// failure on this orchestration host (same workaround as AccessTokenFormatTests).
/// </summary>
public class TokenEndpointRateLimitTests
{
    private const string ServiceClientId = "rate-limit-svc-test";
    private const string ServiceClientSecret = "svc-secret-4097";
    private const string WrongSecret = "definitely-not-the-secret";
    private const int DefaultFailureLimit = 10; // appsettings.json RateLimiting:TokenEndpointFailureLimitPerMinute

    private const string InteractiveClientId = "rate-limit-spa-test";
    private const string RedirectUri = "https://ratelimit.example.test/callback";
    private const string UserEmail = "rate-limit@test.example";
    private const string UserPassword = "Str0ngTestPassw0rd!4097";
    private static readonly Guid TenantId = Guid.Parse("55555555-4097-4097-4097-555555555555");
    private static readonly Guid UserId = Guid.Parse("66666666-4097-4097-4097-666666666666");

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-signing-key-4097" };
    private static readonly RsaSecurityKey EncryptionKey = new(RSA.Create(2048)) { KeyId = "test-encryption-key-4097" };

    /// <summary>
    /// Test-only: maps the X-Test-Client-IP request header onto Connection.RemoteIpAddress before
    /// the application pipeline runs, so different "source IPs" can be simulated over one TestServer.
    /// </summary>
    private sealed class TestClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Client-IP", out var value) &&
                    IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }
                await nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class RateLimitFactory : IAMTestWebApplicationFactory
    {
        private readonly int? _failureLimit;

        public RateLimitFactory(int? failureLimit = null) => _failureLimit = failureLimit;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            if (_failureLimit is int limit)
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["RateLimiting:TokenEndpointFailureLimitPerMinute"] = limit.ToString()
                    });
                });
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, TestClientIpStartupFilter>();

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

    /// <summary>https base address: the IAM.Session cookie is Secure (see AccessTokenFormatTests).</summary>
    private static HttpClient NewClient(RateLimitFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task SeedServiceClientAsync(RateLimitFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = ServiceClientId,
            ClientSecret = ServiceClientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Service account (rate limit test client)",
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                $"{Permissions.Prefixes.Scope}{Scopes.OpenId}"
            }
        });
    }

    private static async Task SeedInteractiveClientAndUserAsync(RateLimitFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Rate Limit Tenant", Type = "Organization", IsActive = true });
        db.Users.Add(new User
        {
            Id = UserId,
            Email = UserEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(UserPassword),
            FirstName = "Rate",
            LastName = "Limit",
            EmailConfirmed = true,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = InteractiveClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "SPA (rate limit test client)",
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

    private static async Task<HttpResponseMessage> ClientCredentialsRequestAsync(
        HttpClient client, string secret, string sourceIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ServiceClientId,
                ["client_secret"] = secret,
                ["scope"] = "openid"
            })
        };
        request.Headers.Add("X-Test-Client-IP", sourceIp);
        return await client.SendAsync(request);
    }

    private static bool IsAuthFailure(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    // ----- tests -----------------------------------------------------------------------------------

    [Fact]
    public async Task WrongSecretFlood_FromOneIp_Gets429WithRetryAfter_AfterThreshold()
    {
        using var factory = new RateLimitFactory();
        await SeedServiceClientAsync(factory);
        var client = NewClient(factory);
        const string attackerIp = "203.0.113.10";

        // The probe from the task: 200 wrong-secret requests from one IP.
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? firstBlocked = null;
        for (var i = 0; i < 200; i++)
        {
            var response = await ClientCredentialsRequestAsync(client, WrongSecret, attackerIp);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && firstBlocked == null)
                firstBlocked = response;
        }

        // Below the threshold: normal auth failures (OpenIddict invalid_client), never 429.
        foreach (var status in statuses.Take(DefaultFailureLimit))
            Assert.True(IsAuthFailure(status), $"expected an auth failure below the threshold, got {status}");

        // At and beyond the threshold: every request is throttled.
        foreach (var status in statuses.Skip(DefaultFailureLimit))
            Assert.Equal(HttpStatusCode.TooManyRequests, status);

        // 429 carries a positive Retry-After (seconds) and an OAuth2-shaped error body.
        Assert.NotNull(firstBlocked);
        var retryAfter = Assert.Single(firstBlocked!.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter), 1, 60);
        var body = JsonSerializer.Deserialize<JsonElement>(await firstBlocked.Content.ReadAsStringAsync());
        Assert.Equal("temporarily_unavailable", body.GetProperty("error").GetString());

        // The throttle is scoped to /connect/token: the same abusive IP still reaches other endpoints.
        var health = new HttpRequestMessage(HttpMethod.Get, "/health");
        health.Headers.Add("X-Test-Client-IP", attackerIp);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(health)).StatusCode);
    }

    [Fact]
    public async Task AbusiveIp_DoesNotLockOut_TheLegitimateClientFromOtherIps()
    {
        using var factory = new RateLimitFactory();
        await SeedServiceClientAsync(factory);
        var client = NewClient(factory);
        const string attackerIp = "203.0.113.66";
        const string legitIp = "198.51.100.7";

        // Exhaust the attacker's budget until it is blocked.
        for (var i = 0; i < DefaultFailureLimit; i++)
            await ClientCredentialsRequestAsync(client, WrongSecret, attackerIp);
        var blocked = await ClientCredentialsRequestAsync(client, WrongSecret, attackerIp);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // The SAME client_id from another IP is unaffected: valid credentials succeed ...
        var ok = await ClientCredentialsRequestAsync(client, ServiceClientSecret, legitIp);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // ... and even a failure from the other IP gets a normal error, not a 429.
        var failure = await ClientCredentialsRequestAsync(client, WrongSecret, legitIp);
        Assert.True(IsAuthFailure(failure.StatusCode), $"expected a normal auth failure, got {failure.StatusCode}");
    }

    [Fact]
    public async Task ValidClientCredentials_AtNormalRates_AreNeverThrottled_EvenAfterSomeFailures()
    {
        using var factory = new RateLimitFactory();
        await SeedServiceClientAsync(factory);
        var client = NewClient(factory);
        const string ip = "198.51.100.20";

        // A few failures below the threshold (e.g. a briefly misconfigured secret) ...
        for (var i = 0; i < DefaultFailureLimit - 1; i++)
        {
            var failure = await ClientCredentialsRequestAsync(client, WrongSecret, ip);
            Assert.True(IsAuthFailure(failure.StatusCode));
        }

        // ... must not throttle the client, and successes never count against the limit.
        for (var i = 0; i < 20; i++)
        {
            var response = await ClientCredentialsRequestAsync(client, ServiceClientSecret, ip);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.False(string.IsNullOrEmpty(payload.GetProperty("access_token").GetString()));
        }
    }

    [Fact]
    public async Task InteractiveLogin_AuthorizationCodeFlow_IsUnaffected()
    {
        using var factory = new RateLimitFactory();
        await SeedInteractiveClientAndUserAsync(factory);
        var client = NewClient(factory);
        client.DefaultRequestHeaders.Add("X-Test-Client-IP", "198.51.100.42");

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = UserEmail, password = UserPassword });
        Assert.True(login.StatusCode == HttpStatusCode.OK,
            $"Login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        var authorizeUrl = "/connect/authorize" +
            $"?client_id={Uri.EscapeDataString(InteractiveClientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=" + Uri.EscapeDataString("openid profile email roles offline_access") +
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
            ["client_id"] = InteractiveClientId
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.StatusCode == HttpStatusCode.OK, $"Token exchange failed: {token.StatusCode} {body}");
        Assert.False(string.IsNullOrEmpty(
            JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Threshold_IsConfigurable_ViaRateLimitingSettings()
    {
        const int customLimit = 3;
        using var factory = new RateLimitFactory(failureLimit: customLimit);
        await SeedServiceClientAsync(factory);
        var client = NewClient(factory);
        const string ip = "203.0.113.99";

        for (var i = 0; i < customLimit; i++)
        {
            var failure = await ClientCredentialsRequestAsync(client, WrongSecret, ip);
            Assert.True(IsAuthFailure(failure.StatusCode),
                $"request {i + 1}/{customLimit} should be a normal auth failure, got {failure.StatusCode}");
        }

        var blocked = await ClientCredentialsRequestAsync(client, WrongSecret, ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.Contains("Retry-After"));
    }
}
