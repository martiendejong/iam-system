using System.Net;
using System.Text;
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

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4706: a deactivated user could still sign in through Google, Microsoft or GitHub because the
/// social callback checked that the provider was active but never that the user was. These tests drive
/// the real SocialAuthService (in-memory DB, stubbed provider HTTP) through the callback and confirm:
/// a deactivated user is refused with "Account is inactive" both for an already-linked social identity
/// and for an account matched by email, a refused attempt writes nothing, and active / auto-created
/// users sign in exactly as before.
/// </summary>
public class SocialLoginInactiveUserTests
{
    private const string RedirectUri = "https://app.example.com/callback";
    private const string InactiveError = "Account is inactive";
    private const string UserEmail = "leaver@example.com";

    // Every stub provider reports this id (Google "sub", Microsoft "id", GitHub numeric "id").
    private const string ProviderUserId = "4706";

    private static readonly DateTime OldTimestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required DbContextOptions<IAMDbContext> Options { get; init; }
        public required IAMDbContext Context { get; init; }
        public required SocialAuthService Service { get; init; }
        public required IdentityProvider Provider { get; init; }

        /// <summary>A fresh context on the same in-memory store, so assertions read what was persisted.</summary>
        public IAMDbContext NewReadContext() => new(Options);

        public async Task<AuthResult> CallbackAsync(string state = "state-1")
        {
            await Service.GetAuthorizationUrlAsync(Provider.Id, RedirectUri, state);
            return await Service.HandleCallbackAsync(Provider.Id, "auth-code", state);
        }
    }

    private static Harness CreateHarness(IdentityProviderType type, bool autoCreateUsers = false)
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new IAMDbContext(options);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5",
            ["SocialAuth:RedirectUri"] = RedirectUri
        }).Build();

        var provider = new IdentityProvider
        {
            Name = $"test-{type}",
            DisplayName = type.ToString(),
            Type = type,
            ClientId = "test-client-id",
            ClientSecret = "test-secret",
            IsActive = true,
            AutoCreateUsers = autoCreateUsers
        };
        context.IdentityProviders.Add(provider);
        context.SaveChanges();

        var service = new SocialAuthService(
            context,
            config,
            new StubHttpClientFactory(new StubProviderHandler(type, UserEmail)),
            new ClaimsMappingService(context),
            new FakeSecretsVaultService(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<SocialAuthService>.Instance);

        return new Harness { Options = options, Context = context, Service = service, Provider = provider };
    }

    private static User AddUser(IAMDbContext context, bool isActive)
    {
        var user = new User
        {
            Email = UserEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            FirstName = "Lee",
            LastName = "Ver",
            EmailConfirmed = true,
            IsActive = isActive,
            LastLoginAt = OldTimestamp
        };
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    private static ExternalLogin AddLink(IAMDbContext context, User user, IdentityProviderType type)
    {
        var link = new ExternalLogin
        {
            UserId = user.Id,
            Provider = type.ToString(),
            ProviderUserId = ProviderUserId,
            Email = "old-" + UserEmail,
            DisplayName = "Old Name",
            LastUsedAt = OldTimestamp
        };
        context.ExternalLogins.Add(link);
        context.SaveChanges();
        return link;
    }

    // --- Refusals ---------------------------------------------------------------------------

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.Microsoft)]
    [InlineData(IdentityProviderType.GitHub)]
    public async Task Callback_DeactivatedUserWithLinkedSocialIdentity_IsRefusedWithNoTokens(IdentityProviderType type)
    {
        var h = CreateHarness(type);
        var user = AddUser(h.Context, isActive: false);
        var link = AddLink(h.Context, user, type);

        var result = await h.CallbackAsync();

        Assert.False(result.Success);
        Assert.Equal(InactiveError, result.Error);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.User);

        using var read = h.NewReadContext();
        Assert.Empty(await read.RefreshTokens.ToListAsync());
        Assert.Equal(OldTimestamp, (await read.Users.SingleAsync(u => u.Id == user.Id)).LastLoginAt);
        var storedLink = await read.ExternalLogins.SingleAsync(l => l.Id == link.Id);
        Assert.Equal(OldTimestamp, storedLink.LastUsedAt);
        Assert.Equal("old-" + UserEmail, storedLink.Email);
        Assert.Equal("Old Name", storedLink.DisplayName);
    }

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.Microsoft)]
    [InlineData(IdentityProviderType.GitHub)]
    public async Task Callback_DeactivatedUserMatchedOnlyByEmail_IsRefusedAndNoExternalLoginIsCreated(IdentityProviderType type)
    {
        // AutoCreateUsers is on: the refusal must not fall through to creating a second account.
        var h = CreateHarness(type, autoCreateUsers: true);
        var user = AddUser(h.Context, isActive: false);

        var result = await h.CallbackAsync();

        Assert.False(result.Success);
        Assert.Equal(InactiveError, result.Error);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);

        using var read = h.NewReadContext();
        Assert.Empty(await read.ExternalLogins.ToListAsync());
        Assert.Empty(await read.RefreshTokens.ToListAsync());
        var users = await read.Users.ToListAsync();
        Assert.Single(users);
        Assert.Equal(OldTimestamp, users[0].LastLoginAt);
        Assert.False(users[0].IsActive);
        Assert.Equal(user.Id, users[0].Id);
    }

    [Fact]
    public async Task Callback_UserDeactivatedAfterLinking_IsRefusedUntilReactivated()
    {
        var h = CreateHarness(IdentityProviderType.Google);
        var user = AddUser(h.Context, isActive: true);
        AddLink(h.Context, user, IdentityProviderType.Google);

        Assert.True((await h.CallbackAsync("state-active")).Success);

        user.IsActive = false;
        await h.Context.SaveChangesAsync();
        var refused = await h.CallbackAsync("state-deactivated");
        Assert.False(refused.Success);
        Assert.Equal(InactiveError, refused.Error);

        user.IsActive = true;
        await h.Context.SaveChangesAsync();
        Assert.True((await h.CallbackAsync("state-reactivated")).Success);
    }

    // --- Regression: unchanged behavior for everyone else ------------------------------------

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.Microsoft)]
    [InlineData(IdentityProviderType.GitHub)]
    public async Task Callback_ActiveUserWithLinkedSocialIdentity_SignsInAndRecordsUsage(IdentityProviderType type)
    {
        var h = CreateHarness(type);
        var user = AddUser(h.Context, isActive: true);
        var link = AddLink(h.Context, user, type);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        Assert.Equal(user.Id, result.User!.Id);

        using var read = h.NewReadContext();
        Assert.Single(await read.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync());
        Assert.True((await read.Users.SingleAsync(u => u.Id == user.Id)).LastLoginAt > OldTimestamp);
        var storedLink = await read.ExternalLogins.SingleAsync(l => l.Id == link.Id);
        Assert.True(storedLink.LastUsedAt > OldTimestamp);
        Assert.Equal(UserEmail, storedLink.Email);
    }

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.Microsoft)]
    [InlineData(IdentityProviderType.GitHub)]
    public async Task Callback_ActiveUserMatchedByEmail_SignsInAndLinksTheSocialIdentity(IdentityProviderType type)
    {
        var h = CreateHarness(type);
        var user = AddUser(h.Context, isActive: true);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));

        using var read = h.NewReadContext();
        var link = await read.ExternalLogins.SingleAsync();
        Assert.Equal(user.Id, link.UserId);
        Assert.Equal(type.ToString(), link.Provider);
        Assert.Equal(ProviderUserId, link.ProviderUserId);
        Assert.Single(await read.RefreshTokens.ToListAsync());
        Assert.True((await read.Users.SingleAsync()).LastLoginAt > OldTimestamp);
    }

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.Microsoft)]
    [InlineData(IdentityProviderType.GitHub)]
    public async Task Callback_UnknownUserWithAutoCreateEnabled_CreatesAnActiveUserAndSignsIn(IdentityProviderType type)
    {
        var h = CreateHarness(type, autoCreateUsers: true);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));

        using var read = h.NewReadContext();
        var created = await read.Users.SingleAsync();
        Assert.True(created.IsActive);
        Assert.Equal(UserEmail, created.Email);
        Assert.Equal(created.Id, result.User!.Id);
        Assert.Equal(created.Id, (await read.ExternalLogins.SingleAsync()).UserId);
        Assert.Single(await read.RefreshTokens.ToListAsync());
    }

    // --- Controller: 400 and no refreshToken cookie -----------------------------------------

    private static SocialAuthController CreateController(ISocialAuthService service)
    {
        return new SocialAuthController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task Controller_Callback_ForDeactivatedUser_Returns400AndSetsNoRefreshTokenCookie()
    {
        var h = CreateHarness(IdentityProviderType.Google);
        var user = AddUser(h.Context, isActive: false);
        AddLink(h.Context, user, IdentityProviderType.Google);
        await h.Service.GetAuthorizationUrlAsync(h.Provider.Id, RedirectUri, "ctl-inactive");
        var controller = CreateController(h.Service);

        var response = await controller.HandleCallback(
            h.Provider.Id, new SocialCallbackRequest("auth-code", "ctl-inactive"));

        var bad = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Equal(400, bad.StatusCode);
        Assert.Equal(InactiveError, bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value));
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.DoesNotContain("refreshToken", controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Controller_Callback_ForActiveUser_StillReturns200AndSetsRefreshTokenCookie()
    {
        var h = CreateHarness(IdentityProviderType.Google);
        var user = AddUser(h.Context, isActive: true);
        AddLink(h.Context, user, IdentityProviderType.Google);
        await h.Service.GetAuthorizationUrlAsync(h.Provider.Id, RedirectUri, "ctl-active");
        var controller = CreateController(h.Service);

        var response = await controller.HandleCallback(
            h.Provider.Id, new SocialCallbackRequest("auth-code", "ctl-active"));

        Assert.IsType<OkObjectResult>(response);
        Assert.Contains("refreshToken=", controller.Response.Headers.SetCookie.ToString());
    }
}

// Test doubles

file class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// Canned provider: the token endpoint returns an access token, the profile endpoint returns a user
/// with a fixed id and email in the shape the given provider type uses.
/// </summary>
file class StubProviderHandler(IdentityProviderType type, string email) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        string json;
        if (url.Contains("/token", StringComparison.Ordinal) || url.Contains("access_token", StringComparison.Ordinal))
        {
            json = """{"access_token":"stub-access-token","token_type":"Bearer"}""";
        }
        else
        {
            json = type switch
            {
                IdentityProviderType.Google =>
                    $$"""{"sub":"4706","email":"{{email}}","name":"Lee Ver","given_name":"Lee","family_name":"Ver"}""",
                IdentityProviderType.Microsoft =>
                    $$"""{"id":"4706","mail":"{{email}}","displayName":"Lee Ver","givenName":"Lee","surname":"Ver"}""",
                IdentityProviderType.GitHub =>
                    $$"""{"id":4706,"email":"{{email}}","name":"Lee Ver","login":"leever"}""",
                _ => throw new InvalidOperationException($"No stub profile for {type}")
            };
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

file class FakeSecretsVaultService : ISecretsVaultService
{
    public Task<SecretEntry> CreateSecretAsync(
        string name, string plainTextValue, Guid? tenantId = null,
        string secretType = "Generic", string? description = null,
        string? rotationScheduleJson = null, string? tags = null,
        Guid? createdByUserId = null, CancellationToken ct = default)
        => Task.FromResult(new SecretEntry { Id = Guid.NewGuid(), Name = name });

    public Task<SecretEntry?> GetSecretAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<SecretEntry?>(null);

    public Task<string?> GetSecretValueAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<List<SecretEntry>> GetSecretsAsync(
        Guid? tenantId = null, string? secretType = null,
        bool? isActive = null, CancellationToken ct = default)
        => Task.FromResult(new List<SecretEntry>());

    public Task<SecretEntry?> UpdateSecretAsync(
        Guid id, string? name = null, string? description = null,
        string? rotationScheduleJson = null, string? tags = null,
        string? secretType = null, bool? isActive = null, CancellationToken ct = default)
        => Task.FromResult<SecretEntry?>(null);

    public Task<SecretEntry> RotateSecretAsync(
        Guid id, string newPlainTextValue, string rotationReason = "Manual",
        Guid? rotatedByUserId = null, TimeSpan? gracePeriod = null, CancellationToken ct = default)
        => Task.FromResult(new SecretEntry { Id = id });

    public Task<List<SecretVersion>> GetSecretHistoryAsync(Guid secretId, CancellationToken ct = default)
        => Task.FromResult(new List<SecretVersion>());

    public Task<bool> DeleteSecretAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<List<SecretEntry>> GetSecretsDueForRotationAsync(CancellationToken ct = default)
        => Task.FromResult(new List<SecretEntry>());
}
