using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using IAM.API.Auth;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5568: a "session" is a real sign-in (an active refresh token). The sessions list shows them, every revoke route
/// ends them, and a revoked sign-in can neither refresh nor keep its IAM.Session cookie.
/// </summary>
public class RevokeSessionsEndRealSignInsTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private const string Password = "Rev0ke!Pass-1";
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly WebApplicationFactory<Program> _noCache;
    private readonly HttpClient _admin;

    public RevokeSessionsEndRealSignInsTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _noCache = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SessionCheckCacheSeconds"] = "0" })));
        _admin = _noCache.CreateClient();
        _admin.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), "admin@test.com", new[] { "SuperAdmin" }));
    }

    private sealed record SignIn(string Access, string Refresh, string SessionCookie);

    private async Task<(Guid UserId, string Email)> CreateUserAsync(string tag)
    {
        var email = $"sessions5568.{tag}@test.com";
        var create = await _admin.PostAsJsonAsync("/api/users", new { email, password = Password, firstName = "Se", lastName = "Ssion" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var id = (await scope.ServiceProvider.GetRequiredService<IAMDbContext>().Users.SingleAsync(u => u.Email == email)).Id;
        return (id, email);
    }

    private async Task<SignIn> SignInAsync(string email)
    {
        var client = _noCache.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var access = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        var cookies = login.Headers.GetValues("Set-Cookie").ToList();
        string Value(string name) => cookies.First(c => c.StartsWith(name + "=")).Split(';')[0].Substring(name.Length + 1);
        return new SignIn(access, Value("refreshToken"), Value("IAM.Session"));
    }

    private HttpRequestMessage Bearer(HttpMethod method, string url, string access)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        return req;
    }

    private async Task<JsonElement> ListAsync(string access)
    {
        var res = await _noCache.CreateClient().SendAsync(Bearer(HttpMethod.Get, "/api/sessions", access));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpStatusCode> RefreshAsync(SignIn s)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.Add("Cookie", $"refreshToken={s.Refresh}; IAM.Session={s.SessionCookie}");
        return (await _noCache.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task List_ShowsActiveSignIns_AndMarksTheCurrentOne()
    {
        var (_, email) = await CreateUserAsync("list");
        var a = await SignInAsync(email);
        await SignInAsync(email);

        var list = await ListAsync(a.Access);

        Assert.Equal(2, list.GetArrayLength());
        var current = list.EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean());
        var aId = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(a.Access)
            .Claims.First(c => c.Type == "refresh_token_id").Value;
        Assert.Equal(aId, current.GetProperty("id").GetString());
    }

    [Fact]
    public async Task RevokeOne_RevokesThatRefreshToken_AndTheOtherStaysUsable()
    {
        var (_, email) = await CreateUserAsync("one");
        var a = await SignInAsync(email);
        var b = await SignInAsync(email);
        var bId = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(b.Access)
            .Claims.First(c => c.Type == "refresh_token_id").Value;

        var del = await _noCache.CreateClient().SendAsync(Bearer(HttpMethod.Delete, $"/api/sessions/{bId}", a.Access));

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(b));
        // Revoking it a second time finds nothing.
        var again = await _noCache.CreateClient().SendAsync(Bearer(HttpMethod.Delete, $"/api/sessions/{bId}", a.Access));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await RefreshAsync(a));
    }

    [Fact]
    public async Task RevokeOthers_ReportsTheRealCount_AndKeepsTheCurrentSignIn()
    {
        var (_, email) = await CreateUserAsync("others");
        var a = await SignInAsync(email);
        var b = await SignInAsync(email);
        var c = await SignInAsync(email);

        var res = await _noCache.CreateClient().SendAsync(Bearer(HttpMethod.Delete, "/api/sessions/revoke-others", a.Access));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revokedCount").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(b));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(c));
        Assert.Equal(HttpStatusCode.OK, await RefreshAsync(a));
    }

    [Fact]
    public async Task RevokeAll_UserRoute_EndsRefreshTokens()
    {
        var (_, email) = await CreateUserAsync("all");
        var a = await SignInAsync(email);
        var b = await SignInAsync(email);

        var res = await _noCache.CreateClient().SendAsync(Bearer(HttpMethod.Delete, "/api/sessions/revoke-all", a.Access));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revokedCount").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(a));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(b));
    }

    [Fact]
    public async Task RevokeAll_AdminRoute_EndsAnotherUsersSignIns()
    {
        var (userId, email) = await CreateUserAsync("admin");
        var a = await SignInAsync(email);

        var res = await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/sessions/revoke-all")
        {
            Content = JsonContent.Create(new { userId })
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revokedCount").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(a));
    }

    [Fact]
    public async Task Refresh_MovesTheSessionCookieToTheNewRefreshToken()
    {
        var (_, email) = await CreateUserAsync("rotate");
        var a = await SignInAsync(email);

        var (status, cookies) = await RefreshRawAsync(a.Refresh, a.SessionCookie);
        Assert.Equal(HttpStatusCode.OK, status);
        var newSession = NonEmptyCookie(cookies, "IAM.Session");
        var newRefresh = NonEmptyCookie(cookies, "refreshToken");
        Assert.NotEqual(a.SessionCookie, newSession);

        // The moved cookie really authorizes: a second refresh only rebinds it when it authenticates (it is not a deletion cookie).
        var (status2, cookies2) = await RefreshRawAsync(newRefresh, newSession);
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.NotEqual(newSession, NonEmptyCookie(cookies2, "IAM.Session"));
    }

    private async Task<(HttpStatusCode Status, List<string> SetCookies)> RefreshRawAsync(string refresh, string session)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.Add("Cookie", $"refreshToken={refresh}; IAM.Session={session}");
        var res = await _noCache.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(req);
        return (res.StatusCode, res.Headers.TryGetValues("Set-Cookie", out var v) ? v.ToList() : new List<string>());
    }

    private static string NonEmptyCookie(List<string> setCookies, string name)
    {
        var value = setCookies.Last(c => c.StartsWith(name + "=")).Split(';')[0].Substring(name.Length + 1);
        Assert.False(string.IsNullOrEmpty(value), $"{name} was deleted instead of rebound");
        return value;
    }

    // ───────────────────────── IAM.Session cookie ─────────────────────────

    private sealed class CookieHarness : IDisposable
    {
        public ServiceProvider Services { get; }
        public IAMDbContext Db { get; }

        public CookieHarness()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jwt:SessionCheckCacheSeconds"] = "0" }).Build());
            services.AddDbContext<IAMDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            services.AddScoped<AccessTokenSessionValidator>();
            services.AddAuthentication().AddCookie("IAM.Session");
            Services = services.BuildServiceProvider();
            Db = Services.GetRequiredService<IAMDbContext>();
        }

        public async Task<(bool Accepted, bool SignedOut)> ValidateAsync(IEnumerable<Claim> claims)
        {
            var http = new DefaultHttpContext { RequestServices = Services };
            var scheme = new AuthenticationScheme("IAM.Session", null, typeof(CookieAuthenticationHandler));
            var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("IAM.Session");
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "IAM.Session"));
            var ctx = new CookieValidatePrincipalContext(http, scheme, options,
                new AuthenticationTicket(principal, new AuthenticationProperties(), "IAM.Session"));

            await AccessTokenSessionValidator.OnValidateCookiePrincipalAsync(ctx);

            return (ctx.Principal != null, http.Response.Headers.ContainsKey("Set-Cookie"));
        }

        public async Task<Guid> AddTokenAsync(DateTime? revokedAt = null)
        {
            var user = new User { Email = $"{Guid.NewGuid():N}@x.com", PasswordHash = "x", FirstName = "a", LastName = "b" };
            Db.Users.Add(user);
            var token = new RefreshToken { UserId = user.Id, TokenHash = Guid.NewGuid().ToString(), ExpiresAt = DateTime.UtcNow.AddDays(1), RevokedAt = revokedAt };
            Db.RefreshTokens.Add(token);
            await Db.SaveChangesAsync();
            return token.Id;
        }

        public void Dispose() => Services.Dispose();
    }

    [Fact]
    public async Task Cookie_BoundToActiveRefreshToken_IsAccepted()
    {
        using var h = new CookieHarness();
        var id = await h.AddTokenAsync();

        var (accepted, _) = await h.ValidateAsync(new[] { new Claim("refresh_token_id", id.ToString()) });

        Assert.True(accepted);
    }

    [Fact]
    public async Task Cookie_BoundToRevokedRefreshToken_IsRejectedAndRemoved()
    {
        using var h = new CookieHarness();
        var id = await h.AddTokenAsync(revokedAt: DateTime.UtcNow);

        var (accepted, signedOut) = await h.ValidateAsync(new[] { new Claim("refresh_token_id", id.ToString()) });

        Assert.False(accepted);
        Assert.True(signedOut);
    }

    [Fact]
    public async Task Cookie_WithoutBinding_IsRejected()
    {
        using var h = new CookieHarness();

        var (accepted, _) = await h.ValidateAsync(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) });

        Assert.False(accepted);
    }

    [Fact]
    public async Task Cookie_AfterRevokeAll_IsRejected()
    {
        using var h = new CookieHarness();
        var id = await h.AddTokenAsync();
        var userId = (await h.Db.RefreshTokens.SingleAsync(t => t.Id == id)).UserId;
        await h.Db.RevokeRefreshTokensAsync(userId);
        await h.Db.SaveChangesAsync();

        var (accepted, _) = await h.ValidateAsync(new[] { new Claim("refresh_token_id", id.ToString()) });

        Assert.False(accepted);
    }
}
