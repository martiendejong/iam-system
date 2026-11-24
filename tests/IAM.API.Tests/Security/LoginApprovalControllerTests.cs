using System.Security.Claims;
using IAM.API.Controllers;
using IAM.API.Tests.Services;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>
/// The whole "approve on another device" path through the controller with the real AuthService: the new browser
/// starts a request, an already signed-in device approves it, and the new browser ends up with its own sign-in
/// (own refresh-token family, refresh cookie, IAM.Session cookie) without ever typing a password.
/// </summary>
public class LoginApprovalControllerTests
{
    private sealed class RecordingAuthentication : IAuthenticationService
    {
        public List<(string? Scheme, ClaimsPrincipal Principal)> SignIns { get; } = new();
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignIns.Add((scheme, principal));
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class NoNotifier : ILoginApprovalNotifier
    {
        public Task NotifyAsync(Guid userId, LoginApproval approval, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Fixture(
        IAMDbContext Context,
        LoginApprovalService Service,
        AuthService Auth,
        RecordingAuthentication Authentication);

    private static Fixture CreateFixture()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var context = new IAMDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
                ["Jwt:Issuer"] = "iam-tests",
                ["Jwt:Audience"] = "iam-tests",
                ["Jwt:AccessTokenExpirationMinutes"] = "5"
            })
            .Build();
        var auth = AuthServiceTestFactory.Create(context, configuration);
        var service = new LoginApprovalService(context, new NoNotifier(), NullLogger<LoginApprovalService>.Instance);
        return new Fixture(context, service, auth, new RecordingAuthentication());
    }

    private static LoginApprovalController CreateController(Fixture f, ClaimsPrincipal? caller = null, string? userAgent = null)
    {
        var services = new ServiceCollection().AddSingleton<IAuthenticationService>(f.Authentication).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        http.Request.Headers.UserAgent = userAgent ?? "Mozilla/5.0 (Linux; Android 14) Chrome/130.0 Mobile Safari/537.36 Brave";
        if (caller != null)
        {
            http.User = caller;
        }

        return new LoginApprovalController(f.Service, f.Auth, NullLogger<LoginApprovalController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static ClaimsPrincipal Principal(Guid userId, Guid? refreshTokenId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (refreshTokenId != null)
        {
            claims.Add(new Claim("refresh_token_id", refreshTokenId.Value.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<User> AddUserAsync(IAMDbContext context, string name, bool active = true)
    {
        var user = new User { Email = $"{name}@example.com", PasswordHash = "x", FirstName = name, LastName = "Test", IsActive = active };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static T Body<T>(IActionResult result, string property)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value!;
        return (T)value.GetType().GetProperty(property)!.GetValue(value)!;
    }

    [Fact]
    public async Task Full_flow_signs_the_new_browser_in_with_its_own_session_and_cookies()
    {
        var f = CreateFixture();
        var user = await AddUserAsync(f.Context, "martien");

        // 1. New browser starts, typing the e-mail address.
        var newBrowser = CreateController(f);
        var start = await newBrowser.Start(new StartApprovalRequest(user.Email, true), default);
        var id = Body<Guid>(start, "id");
        var secret = Body<string>(start, "pollSecret");
        var number = Body<int>(start, "matchNumber");

        // 2. Still pending, and completing now does not sign in.
        Assert.Equal("pending", Body<string>(await newBrowser.Status(id, new ApprovalSecretRequest(secret), default), "status"));
        Assert.IsType<AcceptedResult>(await newBrowser.Complete(id, new ApprovalSecretRequest(secret), default));

        // 3. The signed-in phone sees the request and approves with the number.
        var phone = CreateController(f, Principal(user.Id, Guid.NewGuid()));
        var pending = Assert.IsType<OkObjectResult>(await phone.Pending(default));
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<LoginApprovalInfo>>(pending.Value));
        Assert.IsType<OkObjectResult>(await phone.Approve(id, new ApproveRequest(number), default));

        // 4. The new browser finishes: access token, refresh cookie, IAM.Session cookie.
        var done = await newBrowser.Complete(id, new ApprovalSecretRequest(secret), default);
        var ok = Assert.IsType<OkObjectResult>(done);
        Assert.False(string.IsNullOrEmpty((string?)ok.Value!.GetType().GetProperty("accessToken")!.GetValue(ok.Value)));

        var setCookie = newBrowser.Response.Headers.SetCookie.ToString();
        Assert.Contains("refreshToken=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        var signIn = Assert.Single(f.Authentication.SignIns);
        Assert.Equal("IAM.Session", signIn.Scheme);
        Assert.Equal(user.Id.ToString(), signIn.Principal.FindFirstValue(ClaimTypes.NameIdentifier));

        // 5. The new browser has its own refresh-token family (own session in the sessions list), 90 days (remember me).
        var token = await f.Context.RefreshTokens.SingleAsync(t => t.UserId == user.Id);
        Assert.Equal(token.Id, token.FamilyId);
        Assert.True(token.RememberMe);
        Assert.Contains("Brave", token.UserAgent);

        // 6. The request cannot be used again.
        Assert.IsType<BadRequestObjectResult>(await newBrowser.Complete(id, new ApprovalSecretRequest(secret), default));
        Assert.Single(f.Authentication.SignIns);
    }

    [Fact]
    public async Task A_different_signed_in_user_cannot_approve_a_request_for_someone_else()
    {
        var f = CreateFixture();
        var owner = await AddUserAsync(f.Context, "owner");
        var intruder = await AddUserAsync(f.Context, "intruder");
        var newBrowser = CreateController(f);
        var start = await newBrowser.Start(new StartApprovalRequest(owner.Email, true), default);
        var id = Body<Guid>(start, "id");
        var number = Body<int>(start, "matchNumber");

        var intruderPhone = CreateController(f, Principal(intruder.Id));
        Assert.IsType<NotFoundObjectResult>(await intruderPhone.Get(id, default));
        Assert.IsType<NotFoundObjectResult>(await intruderPhone.Approve(id, new ApproveRequest(number), default));
        Assert.IsType<NotFoundObjectResult>(await intruderPhone.Deny(id, default));

        Assert.IsType<AcceptedResult>(await newBrowser.Complete(id, new ApprovalSecretRequest(Body<string>(start, "pollSecret")), default));
    }

    [Fact]
    public async Task A_stolen_request_id_without_the_secret_cannot_finish_the_sign_in()
    {
        var f = CreateFixture();
        var user = await AddUserAsync(f.Context, "martien");
        var newBrowser = CreateController(f);
        var start = await newBrowser.Start(new StartApprovalRequest(user.Email, true), default);
        var id = Body<Guid>(start, "id");
        var number = Body<int>(start, "matchNumber");
        await CreateController(f, Principal(user.Id)).Approve(id, new ApproveRequest(number), default);

        var attacker = CreateController(f);
        Assert.IsType<BadRequestObjectResult>(await attacker.Complete(id, new ApprovalSecretRequest("guess"), default));
        Assert.IsType<NotFoundObjectResult>(await attacker.Status(id, new ApprovalSecretRequest("guess"), default));
        Assert.Empty(f.Authentication.SignIns);
    }

    [Fact]
    public async Task A_deactivated_account_cannot_finish_even_after_approval()
    {
        var f = CreateFixture();
        var user = await AddUserAsync(f.Context, "martien");
        var newBrowser = CreateController(f);
        var start = await newBrowser.Start(new StartApprovalRequest(user.Email, true), default);
        var id = Body<Guid>(start, "id");
        await CreateController(f, Principal(user.Id)).Approve(id, new ApproveRequest(Body<int>(start, "matchNumber")), default);

        user.IsActive = false;
        await f.Context.SaveChangesAsync();

        Assert.IsType<BadRequestObjectResult>(await newBrowser.Complete(id, new ApprovalSecretRequest(Body<string>(start, "pollSecret")), default));
        Assert.Empty(f.Authentication.SignIns);
        Assert.Empty(await f.Context.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task Starting_too_often_from_one_address_answers_429_with_retry_after()
    {
        var f = CreateFixture();
        var controller = CreateController(f);

        for (var i = 0; i < LoginApprovalService.MaxStartsPerIpPer10Minutes; i++)
        {
            Assert.IsType<OkObjectResult>(await controller.Start(new StartApprovalRequest(null, true), default));
        }

        var limited = Assert.IsType<ObjectResult>(await controller.Start(new StartApprovalRequest(null, true), default));
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
        Assert.Equal("600", controller.Response.Headers["Retry-After"].ToString());
    }
}
