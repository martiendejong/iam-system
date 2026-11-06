using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Auth;
using IAM.API.Tests.Infrastructure;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5628: an access token issued with a refresh token (refresh_token_id claim) is rejected with 401 as soon as
/// that session is revoked or its user is deactivated or locked, instead of being trusted until it expires.
/// </summary>
public class AccessTokenSessionRevocationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private const string Protected = "/api/sessions";
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly WebApplicationFactory<Program> _noCache;
    private readonly HttpClient _admin;

    public AccessTokenSessionRevocationTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        // Cache off: most tests assert the revocation rule itself; the cache window has its own test below.
        _noCache = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SessionCheckCacheSeconds"] = "0" })));
        _admin = _noCache.CreateClient();
        _admin.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), "admin@test.com", new[] { "SuperAdmin" }));
    }

    private async Task<(Guid UserId, string Email, string Access, string Refresh, HttpClient Client)> LoginAsync(string tag)
    {
        var email = $"revoke.{tag}@test.com";
        var create = await _admin.PostAsJsonAsync("/api/users", new { email, password = "Rev0ke!Pass-1", firstName = "Re", lastName = "Voke" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var client = _noCache.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Rev0ke!Pass-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var access = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        var refresh = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("refreshToken=")).Split(';')[0].Substring("refreshToken=".Length);

        using var scope = _factory.Services.CreateScope();
        var userId = (await scope.ServiceProvider.GetRequiredService<IAMDbContext>().Users.SingleAsync(u => u.Email == email)).Id;
        return (userId, email, access, refresh, client);
    }

    private static async Task<HttpStatusCode> CallAsync(HttpClient client, string access)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Protected);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        return (await client.SendAsync(req)).StatusCode;
    }

    private async Task RevokeAllInDbAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        await db.RevokeRefreshTokensAsync(userId);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ActiveSession_TokenIsAccepted()
    {
        var s = await LoginAsync("active");
        Assert.Equal(HttpStatusCode.OK, await CallAsync(s.Client, s.Access));
    }

    [Fact]
    public async Task Logout_RejectsAlreadyIssuedAccessToken()
    {
        var s = await LoginAsync("logout");
        Assert.Equal(HttpStatusCode.OK, await CallAsync(s.Client, s.Access));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        req.Headers.Add("Cookie", $"refreshToken={s.Refresh}");
        Assert.True((await s.Client.SendAsync(req)).IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(s.Client, s.Access));
    }

    [Fact]
    public async Task AdminDeactivation_RejectsAlreadyIssuedAccessToken()
    {
        var s = await LoginAsync("deactivate");
        Assert.Equal(HttpStatusCode.OK, await CallAsync(s.Client, s.Access));

        var deactivate = await _admin.PostAsync($"/api/users/{s.UserId}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(s.Client, s.Access));
    }

    [Fact]
    public async Task RevokeAllSessions_RejectsAlreadyIssuedAccessToken()
    {
        var s = await LoginAsync("revokeall");
        Assert.Equal(HttpStatusCode.OK, await CallAsync(s.Client, s.Access));

        var revoke = await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/sessions/revoke-all")
        {
            Content = JsonContent.Create(new { userId = s.UserId })
        });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(s.Client, s.Access));
    }

    [Fact]
    public async Task PasswordResetAndReplayPaths_RevokeViaSharedHelper_RejectAccessToken()
    {
        // Password reset and refresh-token replay detection both end sessions through RevokeRefreshTokensAsync.
        var s = await LoginAsync("bulkrevoke");
        Assert.Equal(HttpStatusCode.OK, await CallAsync(s.Client, s.Access));

        await RevokeAllInDbAsync(s.UserId);

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(s.Client, s.Access));
    }

    [Fact]
    public async Task LockedOutUser_TokenIsRejected()
    {
        var s = await LoginAsync("locked");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == s.UserId);
            user.IsLockedOut = true;
            user.LockoutEnd = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(s.Client, s.Access));
    }

    [Fact]
    public async Task DeactivatedUser_TokenIsRejected_EvenWithoutRevokedRefreshToken()
    {
        var s = await LoginAsync("inactiveflag");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            (await db.Users.SingleAsync(u => u.Id == s.UserId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(s.Client, s.Access));
    }

    [Fact]
    public async Task TokenWithoutRefreshTokenId_KeepsWorking()
    {
        // Service-account / client-credentials style token: no refresh_token_id claim, no session to check.
        var client = _noCache.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), "admin@test.com", new[] { "SuperAdmin" }));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Protected)).StatusCode);
    }

    [Fact]
    public async Task UnknownRefreshTokenId_IsRejected()
    {
        var forged = TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), "admin@test.com", new[] { "SuperAdmin" },
            "11111111-1111-1111-1111-111111111111", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(_noCache.CreateClient(), forged));
    }

    [Fact]
    public async Task FailingLookup_RejectsRequest_FailClosed()
    {
        var s = await LoginAsync("failclosed");
        var client = _noCache.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddScoped<AccessTokenSessionValidator, ThrowingValidator>())).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(client, s.Access));
    }

    [Fact]
    public async Task Burst_CausesOneLookupPerCacheWindow_AndRevocationBecomesVisibleAfterIt()
    {
        var s = await LoginAsync("burst");
        CountingValidator.Lookups = 0;
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jwt:SessionCheckCacheSeconds"] = "2" }));
            b.ConfigureTestServices(services => services.AddScoped<AccessTokenSessionValidator, CountingValidator>());
        }).CreateClient();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await CallAsync(client, s.Access));
        }
        Assert.Equal(1, CountingValidator.Lookups);

        await RevokeAllInDbAsync(s.UserId);

        // Still served from the cache inside the window ...
        Assert.Equal(HttpStatusCode.OK, await CallAsync(client, s.Access));
        // ... and rejected once the window has passed.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(client, s.Access));
        Assert.Equal(2, CountingValidator.Lookups);
    }

    private sealed class ThrowingValidator : AccessTokenSessionValidator
    {
        public ThrowingValidator(IAMDbContext db, IMemoryCache cache, IConfiguration cfg, ILogger<AccessTokenSessionValidator> log)
            : base(db, cache, cfg, log) { }
        protected override Task<bool> LookupAsync(Guid refreshTokenId, CancellationToken ct) => throw new InvalidOperationException("db down");
    }

    private sealed class CountingValidator : AccessTokenSessionValidator
    {
        public static int Lookups;
        public CountingValidator(IAMDbContext db, IMemoryCache cache, IConfiguration cfg, ILogger<AccessTokenSessionValidator> log)
            : base(db, cache, cfg, log) { }
        protected override Task<bool> LookupAsync(Guid refreshTokenId, CancellationToken ct)
        {
            Interlocked.Increment(ref Lookups);
            return base.LookupAsync(refreshTokenId, ct);
        }
    }
}
