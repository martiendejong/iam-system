using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 1743: POST /auth/login's "rememberMe" flag must extend both the IAM.Session
/// cookie (persistent + ~30 day ExpiresUtc instead of a session-only cookie relying on
/// Program.cs's 8h sliding window) and the refreshToken cookie (Expires at least 30
/// days out) - and leave today's unchecked behavior untouched.
/// </summary>
public class AuthControllerRememberMeTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly HttpClient _superAdminClient;

    public AuthControllerRememberMeTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _superAdminClient = factory.CreateClient();
        _superAdminClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            "admin@test.com",
            new[] { "SuperAdmin" }));
    }

    private HttpClient CreateRawCookieClient()
    {
        // Disable the test client's own cookie handling so Set-Cookie response headers
        // stay on the response for direct inspection instead of being absorbed into an
        // internal CookieContainer.
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    private async Task<string> CreateActiveUserAsync(string email, string password)
    {
        var response = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email,
            password,
            firstName = "Remember",
            lastName = "Me"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return email;
    }

    private static IEnumerable<string> SetCookieHeaders(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values) ? values : Enumerable.Empty<string>();
    }

    private static string GetCookie(HttpResponseMessage response, string cookieName)
    {
        var cookie = SetCookieHeaders(response).FirstOrDefault(c => c.StartsWith($"{cookieName}=", StringComparison.OrdinalIgnoreCase));
        Assert.True(cookie != null, $"Expected a Set-Cookie header for '{cookieName}'. Got: {string.Join(" | ", SetCookieHeaders(response))}");
        return cookie!;
    }

    private static DateTimeOffset ParseExpiresAttribute(string setCookieHeader)
    {
        var part = setCookieHeader.Split(';')
            .Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.True(part != null, $"Expected an 'expires' attribute on cookie: {setCookieHeader}");
        return DateTimeOffset.Parse(part!.Substring("expires=".Length));
    }

    [Fact]
    public async Task Login_WithRememberMeTrue_IssuesPersistentSessionCookieAndExtendedRefreshCookie()
    {
        var email = "remember.me.checked@test.com";
        await CreateActiveUserAsync(email, "R3memberMe!Pass");
        var client = CreateRawCookieClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "R3memberMe!Pass",
            rememberMe = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var refreshCookie = GetCookie(response, "refreshToken");
        var refreshExpires = ParseExpiresAttribute(refreshCookie);
        Assert.True((refreshExpires - DateTimeOffset.UtcNow).TotalDays >= 29.5,
            $"Expected refreshToken to expire >= ~30 days out when rememberMe=true, got {refreshExpires}");

        var sessionCookie = GetCookie(response, "IAM.Session");
        var sessionExpires = ParseExpiresAttribute(sessionCookie);
        Assert.True((sessionExpires - DateTimeOffset.UtcNow).TotalDays >= 29.5,
            $"Expected IAM.Session to expire >= ~30 days out (persistent) when rememberMe=true, got {sessionExpires}");
    }

    [Fact]
    public async Task Login_WithRememberMeFalse_KeepsTodaysDefaultRefreshLifetimeAndSessionOnlyCookie()
    {
        var email = "remember.me.unchecked@test.com";
        await CreateActiveUserAsync(email, "N0RememberMe!Pass");
        var client = CreateRawCookieClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "N0RememberMe!Pass",
            rememberMe = false
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var refreshCookie = GetCookie(response, "refreshToken");
        var refreshExpires = ParseExpiresAttribute(refreshCookie);
        // No TokenConfiguration exists for this fresh user/tenant, so today's 7-day
        // default applies (see TokenLifetimeConfigurationTests) - well under the 30-day
        // remember-me floor, proving rememberMe=false did not extend it.
        Assert.True((refreshExpires - DateTimeOffset.UtcNow).TotalDays < 29.5,
            $"Expected refreshToken to keep today's short default when rememberMe=false, got {refreshExpires}");

        var sessionCookie = GetCookie(response, "IAM.Session");
        // A non-persistent cookie auth ticket carries no Expires/Max-Age attribute on the
        // Set-Cookie header - the browser treats it as a session cookie deleted on close,
        // matching today's (pre-remember-me) behavior exactly.
        Assert.DoesNotContain("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithoutRememberMeField_DefaultsToUnchecked()
    {
        var email = "remember.me.omitted@test.com";
        await CreateActiveUserAsync(email, "0mittedField!Pass");
        var client = CreateRawCookieClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "0mittedField!Pass"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sessionCookie = GetCookie(response, "IAM.Session");
        Assert.DoesNotContain("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);
    }
}
