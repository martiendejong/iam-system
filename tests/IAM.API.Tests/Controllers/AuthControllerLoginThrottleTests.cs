using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.API.Tests.Services;
using IAM.Core.Services;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5166 over HTTP: POST /api/auth/login answers an unknown email and a real account with a
/// wrong password identically, both reach 429 + Retry-After on the same attempt, and neither a
/// spoofed X-Forwarded-For nor another address changes that.
/// </summary>
public class AuthControllerLoginThrottleTests : IClassFixture<AuthControllerLoginThrottleTests.RemoteIpFactory>
{
    private const string RealPassword = "Throttle-Test-9!";

    private readonly RemoteIpFactory _factory;
    private readonly HttpClient _superAdminClient;

    public AuthControllerLoginThrottleTests(RemoteIpFactory factory)
    {
        _factory = factory;
        _superAdminClient = factory.CreateClient();
        _superAdminClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            "admin@test.com",
            new[] { "SuperAdmin" }));
    }

    /// <summary>
    /// TestServer has no socket, so RemoteIpAddress is null and UseForwardedHeaders would trust any
    /// X-Forwarded-For. This gives every request a real, untrusted remote address (settable per
    /// request with X-Test-Remote-Ip), which is what a public caller looks like in production.
    /// </summary>
    public class RemoteIpFactory : IAMTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddTransient<IStartupFilter, RemoteIpStartupFilter>();

                // A throttle on a frozen clock: the 2 s wait after the 4th failure must not run out
                // between two requests when a BCrypt check is slow on a busy host (it did, in a full
                // suite run). The wait is then exactly 2 s and Retry-After is always "2".
                services.RemoveAll<ILoginThrottle>();
                services.AddSingleton<ILoginThrottle>(sp => new LoginThrottle(
                    sp.GetRequiredService<IMemoryCache>(),
                    sp.GetRequiredService<IConfiguration>(),
                    sp.GetRequiredService<ILogger<LoginThrottle>>(),
                    new ManualTimeProvider()));
            });
        }
    }

    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                var ip = context.Request.Headers["X-Test-Remote-Ip"].FirstOrDefault() ?? "198.51.100.7";
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                return nextMiddleware();
            });
            next(app);
        };
    }

    private async Task<string> CreateUserAsync(string email)
    {
        var response = await _superAdminClient.PostAsJsonAsync("/api/users", new
        {
            email,
            password = RealPassword,
            firstName = "Throttle",
            lastName = "Test"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return email;
    }

    private async Task<HttpResponseMessage> LoginAsync(string email, string password, string? remoteIp = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password })
        };
        if (remoteIp != null) request.Headers.Add("X-Test-Remote-Ip", remoteIp);
        if (forwardedFor != null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static async Task<(HttpStatusCode Status, string? RetryAfter, string Body)> Snapshot(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values) ? values.Single() : null;
        return (response.StatusCode, retryAfter, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownEmail_AndRealEmailWithAWrongPassword_GetIdenticalAnswersAndTheSameWait()
    {
        var real = await CreateUserAsync("throttle.real@test.com");

        var unknownAnswers = new List<(HttpStatusCode Status, string? RetryAfter, string Body)>();
        var realAnswers = new List<(HttpStatusCode Status, string? RetryAfter, string Body)>();
        for (var i = 0; i < 6; i++)
        {
            unknownAnswers.Add(await Snapshot(await LoginAsync("throttle.nobody@test.com", "wrong-password", "198.51.100.21")));
            realAnswers.Add(await Snapshot(await LoginAsync(real, "wrong-password", "198.51.100.22")));
        }

        // Status, Retry-After and body are the same, attempt by attempt, whether or not the account exists.
        Assert.Equal(unknownAnswers, realAnswers);

        // Attempts 1-4 are the normal failure; from the 5th on the caller is told to wait.
        Assert.All(realAnswers.Take(4), a =>
        {
            Assert.Equal(HttpStatusCode.BadRequest, a.Status);
            Assert.Null(a.RetryAfter);
            Assert.Equal("""{"error":"Invalid email or password"}""", a.Body);
        });
        Assert.All(realAnswers.Skip(4), a =>
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, a.Status);
            Assert.Equal("2", a.RetryAfter);
            Assert.DoesNotContain("lock", a.Body, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ASpoofedXForwardedForDoesNotEscapeTheThrottle()
    {
        var real = await CreateUserAsync("throttle.spoof@test.com");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            // A new made-up client address on every request; the remote address is not a trusted proxy.
            statuses.Add((await LoginAsync(real, "wrong-password", "198.51.100.31", forwardedFor: $"203.0.113.{100 + i}")).StatusCode);
        }

        Assert.Equal(
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests },
            statuses);
    }

    [Fact]
    public async Task TheOwnerCanStillSignInFromAnotherAddress_WhileAnAttackerIsBeingSlowedDown()
    {
        var real = await CreateUserAsync("throttle.owner@test.com");
        for (var i = 0; i < 8; i++) await LoginAsync(real, "wrong-password", "198.51.100.41");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(real, "wrong-password", "198.51.100.41")).StatusCode);

        var owner = await LoginAsync(real, RealPassword, "198.51.100.42");

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Contains("accessToken", await owner.Content.ReadAsStringAsync());
    }
}
