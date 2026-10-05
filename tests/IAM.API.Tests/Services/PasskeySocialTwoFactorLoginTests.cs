using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4573: an account enrolled in 2FA could skip it entirely by signing in with a passkey or through a
/// social / SSO provider, because both paths issued tokens directly. They now go through the same gate as
/// password / magic-link / OTP login (IAuthService.CompletePasswordlessLoginAsync): a 2FA account gets
/// { requiresTwoFactor: true, userId } with no token and no cookie, the existing verify-code step then
/// issues the tokens, and an account without 2FA signs in immediately, exactly as before.
///
/// The service-level tests drive the real SocialAuthService and AuthService (in-memory DB, stubbed provider
/// HTTP); the controller-level tests drive the real PasskeyController / SocialAuthController. Where a test
/// needs "the gate says: challenge" without caring which factor (TOTP), a spy stands in for IAuthService and
/// throws on any other call - so a controller or service that mints tokens directly fails the test.
/// </summary>
public class PasskeySocialTwoFactorLoginTests
{
    private const string RedirectUri = "https://app.example.com/callback";
    private const string UserEmail = "owner@example.com";
    private const string ProviderUserId = "4573";

    private static readonly DateTime OldTimestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5",
            ["SocialAuth:RedirectUri"] = RedirectUri
        }).Build();

    private static (DbContextOptions<IAMDbContext> Options, IAMDbContext Context) CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return (options, new IAMDbContext(options));
    }

    private static User AddUser(IAMDbContext context, TwoFactorMethod method)
    {
        var user = new User
        {
            Email = UserEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            FirstName = "Own",
            LastName = "Er",
            EmailConfirmed = true,
            IsActive = true,
            TwoFactorEnabled = method != TwoFactorMethod.None,
            TwoFactorMethod = method
        };
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    // --- Social / SSO ------------------------------------------------------------------------

    private sealed class SocialHarness
    {
        public required DbContextOptions<IAMDbContext> Options { get; init; }
        public required IAMDbContext Context { get; init; }
        public required SocialAuthService Service { get; init; }
        public required AuthService AuthService { get; init; }
        public required RecordingEmailService Email { get; init; }
        public required IdentityProvider Provider { get; init; }

        /// <summary>A fresh context on the same in-memory store, so assertions read what was persisted.</summary>
        public IAMDbContext NewReadContext() => new(Options);

        public async Task<AuthResult> CallbackAsync(string state = "state-1")
        {
            await Service.GetAuthorizationUrlAsync(Provider.Id, RedirectUri, state);
            return await Service.HandleCallbackAsync(Provider.Id, "auth-code", state);
        }
    }

    /// <param name="gate">Replaces the real AuthService as the sign-in gate (a spy). Null = the real one.</param>
    private static SocialHarness CreateSocialHarness(bool autoCreateUsers = false, IAuthService? gate = null)
    {
        var (options, context) = CreateContext();
        var config = CreateConfiguration();

        var provider = new IdentityProvider
        {
            Name = "test-google",
            DisplayName = "Google",
            Type = IdentityProviderType.Google,
            ClientId = "test-client-id",
            ClientSecret = "test-secret",
            IsActive = true,
            AutoCreateUsers = autoCreateUsers
        };
        context.IdentityProviders.Add(provider);
        context.SaveChanges();

        var email = new RecordingEmailService();
        var authService = AuthServiceTestFactory.Create(context, config, email);

        var service = new SocialAuthService(
            context,
            config,
            new GoogleStubHttpClientFactory(new GoogleStubHandler(ProviderUserId, UserEmail)),
            MethodStub.New<ISecretsVaultService>().Proxy,
            new MemoryCache(new MemoryCacheOptions()),
            gate ?? authService,
            NullLogger<SocialAuthService>.Instance);

        return new SocialHarness
        {
            Options = options,
            Context = context,
            Service = service,
            AuthService = authService,
            Email = email,
            Provider = provider
        };
    }

    private static void AddGoogleLink(IAMDbContext context, User user)
    {
        context.ExternalLogins.Add(new ExternalLogin
        {
            UserId = user.Id,
            Provider = IdentityProviderType.Google.ToString(),
            ProviderUserId = ProviderUserId,
            Email = UserEmail,
            DisplayName = "Own Er",
            LastUsedAt = OldTimestamp
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task SocialLogin_LinkedAccountWithEmailTwoFactor_ReturnsChallengeAndNoTokens()
    {
        var h = CreateSocialHarness();
        var user = AddUser(h.Context, TwoFactorMethod.Email);
        AddGoogleLink(h.Context, user);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Equal(user.Id, result.User!.Id);

        var sent = Assert.Single(h.Email.SentTwoFactorCodes);
        Assert.Equal(UserEmail, sent.Email);

        using var read = h.NewReadContext();
        Assert.Empty(await read.RefreshTokens.ToListAsync());
        // The login is not complete yet: it must not count as a last login.
        Assert.Null((await read.Users.SingleAsync(u => u.Id == user.Id)).LastLoginAt);
    }

    [Fact]
    public async Task SocialLogin_AccountMatchedByVerifiedEmailWithEmailTwoFactor_ReturnsChallengeAndNoTokens()
    {
        // No ExternalLogin yet: the provider's verified e-mail matches the (confirmed) account, so it
        // is linked on this sign-in - the link must not be a way around the second factor.
        var h = CreateSocialHarness();
        var user = AddUser(h.Context, TwoFactorMethod.Email);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Single(h.Email.SentTwoFactorCodes);

        using var read = h.NewReadContext();
        Assert.Empty(await read.RefreshTokens.ToListAsync());
        Assert.Equal(user.Id, (await read.ExternalLogins.SingleAsync()).UserId);
    }

    [Fact]
    public async Task SocialLogin_ThenVerifyCode_IssuesTokens()
    {
        var h = CreateSocialHarness();
        var user = AddUser(h.Context, TwoFactorMethod.Email);
        AddGoogleLink(h.Context, user);

        var challenge = await h.CallbackAsync();
        Assert.True(challenge.RequiresTwoFactor);
        var code = h.Email.SentTwoFactorCodes[0].Code;

        var verified = await h.AuthService.VerifyLoginTwoFactorAsync(user.Id, code);

        Assert.True(verified.Success);
        Assert.False(verified.RequiresTwoFactor);
        Assert.False(string.IsNullOrEmpty(verified.AccessToken));
        Assert.False(string.IsNullOrEmpty(verified.RefreshToken));

        using var read = h.NewReadContext();
        Assert.Single(await read.RefreshTokens.ToListAsync());
        Assert.NotNull((await read.Users.SingleAsync(u => u.Id == user.Id)).LastLoginAt);
    }

    [Fact]
    public async Task SocialLogin_ThenWrongCode_DoesNotIssueTokens()
    {
        var h = CreateSocialHarness();
        var user = AddUser(h.Context, TwoFactorMethod.Email);
        AddGoogleLink(h.Context, user);
        await h.CallbackAsync();

        var verified = await h.AuthService.VerifyLoginTwoFactorAsync(user.Id, "000000");

        Assert.False(verified.Success);
        Assert.Null(verified.AccessToken);
        using var read = h.NewReadContext();
        Assert.Empty(await read.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task SocialLogin_LinkedAccountWithoutTwoFactor_IssuesTokensImmediately()
    {
        var h = CreateSocialHarness();
        var user = AddUser(h.Context, TwoFactorMethod.None);
        AddGoogleLink(h.Context, user);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.False(result.RequiresTwoFactor);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        Assert.True(result.RefreshTokenLifetimeDays > 0);
        Assert.Equal(user.Id, result.User!.Id);
        Assert.Empty(h.Email.SentTwoFactorCodes);

        using var read = h.NewReadContext();
        Assert.Single(await read.RefreshTokens.ToListAsync());
        Assert.NotNull((await read.Users.SingleAsync(u => u.Id == user.Id)).LastLoginAt);
    }

    [Fact]
    public async Task SocialLogin_FreshlyAutoCreatedAccount_SignsInImmediately()
    {
        // A brand-new account has no 2FA enrolled, so the gate lets it straight through.
        var h = CreateSocialHarness(autoCreateUsers: true);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.False(result.RequiresTwoFactor);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.Equal(UserEmail, result.User!.Email);
        Assert.Empty(h.Email.SentTwoFactorCodes);

        using var read = h.NewReadContext();
        var created = await read.Users.SingleAsync();
        Assert.False(created.TwoFactorEnabled);
        Assert.Single(await read.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task SocialLogin_FreshlyAutoCreatedAccount_IsHandedToTheGateNotSignedInDirectly()
    {
        // The auto-create path must run the same gate as an existing account. A spy gate that always
        // demands a second factor proves it: the service returns that challenge, creates no token
        // itself, and never calls any other IAuthService member (the spy throws on those).
        var (spyService, spy) = MethodStub.New<IAuthService>();
        spy.On(nameof(IAuthService.CompletePasswordlessLoginAsync), args =>
            Task.FromResult(new AuthResult { Success = true, RequiresTwoFactor = true, User = (User)args[0]! }));
        var h = CreateSocialHarness(autoCreateUsers: true, gate: spyService);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Equal(new[] { nameof(IAuthService.CompletePasswordlessLoginAsync) }, spy.Calls);

        using var read = h.NewReadContext();
        Assert.Empty(await read.RefreshTokens.ToListAsync());
        Assert.Equal(UserEmail, (await read.Users.SingleAsync()).Email);
    }

    [Fact]
    public async Task SocialLogin_ExistingAccount_IsHandedToTheGateNotSignedInDirectly()
    {
        var (spyService, spy) = MethodStub.New<IAuthService>();
        spy.On(nameof(IAuthService.CompletePasswordlessLoginAsync), args =>
            Task.FromResult(new AuthResult { Success = true, RequiresTwoFactor = true, User = (User)args[0]! }));
        var h = CreateSocialHarness(gate: spyService);
        var user = AddUser(h.Context, TwoFactorMethod.Totp);
        AddGoogleLink(h.Context, user);

        var result = await h.CallbackAsync();

        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.AccessToken);
        Assert.Equal(new[] { nameof(IAuthService.CompletePasswordlessLoginAsync) }, spy.Calls);
        using var read = h.NewReadContext();
        Assert.Empty(await read.RefreshTokens.ToListAsync());
    }

    // --- SocialAuthController ----------------------------------------------------------------

    private static SocialAuthController CreateSocialController(AuthResult callbackResult)
    {
        var (socialService, stub) = MethodStub.New<ISocialAuthService>();
        stub.On(nameof(ISocialAuthService.HandleCallbackAsync), _ => Task.FromResult(callbackResult));
        return new SocialAuthController(socialService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task SocialController_WhenTwoFactorRequired_ReturnsChallengeWithoutCookieOrToken()
    {
        var user = new User { Id = Guid.NewGuid(), Email = UserEmail, FirstName = "Own", LastName = "Er" };
        var controller = CreateSocialController(new AuthResult { Success = true, RequiresTwoFactor = true, User = user });

        var response = await controller.HandleCallback(Guid.NewGuid(), new SocialCallbackRequest("code", "state"));

        var body = OkBody(response);
        Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.Equal(user.Id, body.GetProperty("userId").GetGuid());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.True(StringValues.IsNullOrEmpty(controller.Response.Headers.SetCookie));
    }

    [Fact]
    public async Task SocialController_WithoutTwoFactor_SetsCookieAndReturnsAccessToken()
    {
        var user = new User { Id = Guid.NewGuid(), Email = UserEmail, FirstName = "Own", LastName = "Er" };
        var controller = CreateSocialController(new AuthResult
        {
            Success = true,
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            RefreshTokenLifetimeDays = 7,
            User = user
        });

        var response = await controller.HandleCallback(Guid.NewGuid(), new SocialCallbackRequest("code", "state"));

        var body = OkBody(response);
        Assert.Equal("access-token", body.GetProperty("accessToken").GetString());
        Assert.False(body.TryGetProperty("requiresTwoFactor", out _));
        Assert.Contains("refreshToken=refresh-token", controller.Response.Headers.SetCookie.ToString());
    }

    // --- PasskeyController -------------------------------------------------------------------

    private sealed record PasskeyHarness(
        PasskeyController Controller,
        IAMDbContext Context,
        AuthService AuthService,
        RecordingEmailService Email,
        DbContextOptions<IAMDbContext> Options);

    /// <param name="gate">Replaces the real AuthService as the sign-in gate (a spy). Null = the real one.</param>
    private static PasskeyHarness CreatePasskeyHarness(Func<IAMDbContext, Guid> addUser, IAuthService? gate = null)
    {
        var (options, context) = CreateContext();
        var email = new RecordingEmailService();
        var authService = AuthServiceTestFactory.Create(context, CreateConfiguration(), email);
        var userId = addUser(context);

        var (passkeyService, passkeyStub) = MethodStub.New<IPasskeyService>();
        passkeyStub.On(nameof(IPasskeyService.CompleteAuthenticationAsync), _ => Task.FromResult<Guid?>(userId));

        var controller = new PasskeyController(passkeyService, gate ?? authService, context, NullLogger<PasskeyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return new PasskeyHarness(controller, context, authService, email, options);
    }

    private static Task<ActionResult> CompletePasskeyAsync(PasskeyController controller) =>
        controller.CompleteAuthentication(new AuthenticatorAssertionRawResponse(), CancellationToken.None);

    [Fact]
    public async Task Passkey_WithEmailTwoFactor_ReturnsChallengeAndNoTokensOrCookie()
    {
        Guid userId = default;
        var h = CreatePasskeyHarness(c => userId = AddUser(c, TwoFactorMethod.Email).Id);

        var response = await CompletePasskeyAsync(h.Controller);

        var body = OkBody(response);
        Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.Equal(userId, body.GetProperty("userId").GetGuid());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.True(StringValues.IsNullOrEmpty(h.Controller.Response.Headers.SetCookie));

        var sent = Assert.Single(h.Email.SentTwoFactorCodes);
        Assert.Equal(UserEmail, sent.Email);
        using var read = new IAMDbContext(h.Options);
        Assert.Empty(await read.RefreshTokens.ToListAsync());
        Assert.Null((await read.Users.SingleAsync()).LastLoginAt);
    }

    [Fact]
    public async Task Passkey_ThenVerifyCode_IssuesTokens()
    {
        Guid userId = default;
        var h = CreatePasskeyHarness(c => userId = AddUser(c, TwoFactorMethod.Email).Id);
        OkBody(await CompletePasskeyAsync(h.Controller));
        var code = h.Email.SentTwoFactorCodes[0].Code;

        var verified = await h.AuthService.VerifyLoginTwoFactorAsync(userId, code);

        Assert.True(verified.Success);
        Assert.False(string.IsNullOrEmpty(verified.AccessToken));
        Assert.False(string.IsNullOrEmpty(verified.RefreshToken));
        using var read = new IAMDbContext(h.Options);
        Assert.Single(await read.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task Passkey_WithoutTwoFactor_IssuesTokensAndCookieImmediately()
    {
        var h = CreatePasskeyHarness(c => AddUser(c, TwoFactorMethod.None).Id);

        var response = await CompletePasskeyAsync(h.Controller);

        var body = OkBody(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.False(body.TryGetProperty("requiresTwoFactor", out _));
        Assert.Equal(UserEmail, body.GetProperty("user").GetProperty("email").GetString());
        Assert.Contains("refreshToken=", h.Controller.Response.Headers.SetCookie.ToString());
        Assert.Empty(h.Email.SentTwoFactorCodes);
        using var read = new IAMDbContext(h.Options);
        Assert.Single(await read.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task Passkey_WhenGateDemandsAFactor_ReturnsChallengeAndNeverMintsTokensDirectly()
    {
        // A TOTP-enrolled user: whatever the gate decides (email code today, authenticator code once the
        // TOTP gate lands), the controller must surface the challenge and must not call anything but
        // CompletePasswordlessLoginAsync - in particular not LoginBypassPasswordAsync.
        var (spyService, spy) = MethodStub.New<IAuthService>();
        spy.On(nameof(IAuthService.CompletePasswordlessLoginAsync), args =>
            Task.FromResult(new AuthResult { Success = true, RequiresTwoFactor = true, User = (User)args[0]! }));
        Guid userId = default;
        var h = CreatePasskeyHarness(c => userId = AddUser(c, TwoFactorMethod.Totp).Id, gate: spyService);

        var response = await CompletePasskeyAsync(h.Controller);

        var body = OkBody(response);
        Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.Equal(userId, body.GetProperty("userId").GetGuid());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.True(StringValues.IsNullOrEmpty(h.Controller.Response.Headers.SetCookie));
        Assert.Equal(new[] { nameof(IAuthService.CompletePasswordlessLoginAsync) }, spy.Calls);
    }

    // --- helpers -----------------------------------------------------------------------------

    /// <summary>The anonymous 200 body as JSON, so the tests assert the wire shape a client sees.</summary>
    private static JsonElement OkBody(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static JsonElement OkBody(ActionResult result) => OkBody((IActionResult)result);
}

/// <summary>
/// Stands in for an interface without hand-writing every member (the interface grows over time):
/// handlers are registered per method name, every call is recorded, and an unregistered call throws -
/// so code that reaches for a member the test did not expect fails loudly.
/// </summary>
public class MethodStub : DispatchProxy
{
    private readonly Dictionary<string, Func<object?[], object?>> _handlers = new();

    public List<string> Calls { get; } = new();

    public static (T Proxy, MethodStub Stub) New<T>() where T : class
    {
        var proxy = Create<T, MethodStub>();
        return (proxy, (MethodStub)(object)proxy);
    }

    public MethodStub On(string method, Func<object?[], object?> handler)
    {
        _handlers[method] = handler;
        return this;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        Calls.Add(name);

        if (!_handlers.TryGetValue(name, out var handler))
            throw new InvalidOperationException($"Unexpected call to {targetMethod.DeclaringType?.Name}.{name}");

        return handler(args ?? Array.Empty<object?>());
    }
}

// Test doubles for the Google provider

file class GoogleStubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

file class GoogleStubHandler(string providerUserId, string email) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        var json = url.Contains("/token", StringComparison.Ordinal) || url.Contains("access_token", StringComparison.Ordinal)
            ? """{"access_token":"stub-access-token","token_type":"Bearer"}"""
            : $$"""{"sub":"{{providerUserId}}","email":"{{email}}","email_verified":true,"name":"Own Er","given_name":"Own","family_name":"Er"}""";

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}
