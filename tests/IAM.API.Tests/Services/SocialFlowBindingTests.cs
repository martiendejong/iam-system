using System.Net;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5493: a social flow is bound to the browser that started it. State alone is not enough (login CSRF /
/// attacker-account linking): the callback and the link need the one-time browser secret returned next to the
/// state, a state only completes the flow it was issued for, and a link state only works for its own user.
/// </summary>
public class SocialFlowBindingTests
{
    private const string RedirectUri = "https://app.example.com/callback";
    private const string Secret = "browser-secret-5493";

    private sealed class Clock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class Harness
    {
        public required IAMDbContext Context { get; init; }
        public required SocialAuthService Service { get; init; }
        public required IdentityProvider Provider { get; init; }
        public required CountingHandler Http { get; init; }
        public required Clock Clock { get; init; }
        public required DbContextOptions<IAMDbContext> Options { get; init; }
    }

    private static Harness Create()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
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
            Name = "g", DisplayName = "Google", Type = IdentityProviderType.Google,
            ClientId = "cid", ClientSecret = "csecret", IsActive = true, AutoCreateUsers = true
        };
        context.IdentityProviders.Add(provider);
        context.SaveChanges();

        var http = new CountingHandler();
        var clock = new Clock();
        var service = new SocialAuthService(
            context, config, new Factory(http), new Vault(),
            new MemoryCache(new MemoryCacheOptions { Clock = clock }),
            AuthServiceTestFactory.Create(context, config), NullLogger<SocialAuthService>.Instance);
        return new Harness { Context = context, Service = service, Provider = provider, Http = http, Clock = clock, Options = options };
    }

    private static User AddUser(IAMDbContext context, string email = "owner@example.com")
    {
        var user = new User
        {
            Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            FirstName = "O", LastName = "W", EmailConfirmed = true, IsActive = true
        };
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    private static Task<string> StartLogin(Harness h, string state = "s-login") =>
        h.Service.GetAuthorizationUrlAsync(h.Provider.Id, RedirectUri, state, Secret);

    private static Task<string> StartLink(Harness h, Guid userId, string state = "s-link") =>
        h.Service.GetAuthorizationUrlAsync(h.Provider.Id, RedirectUri, state, Secret, SocialFlowPurpose.Link, userId);

    private static async Task AssertNothingHappenedAsync(Harness h, AuthResult result)
    {
        Assert.False(result.Success);
        Assert.Null(result.AccessToken);
        Assert.Equal(0, h.Http.Calls); // the code was never exchanged
        using var read = new IAMDbContext(h.Options);
        Assert.Empty(await read.ExternalLogins.ToListAsync());
        Assert.Empty(await read.Users.ToListAsync());
    }

    // ----- sign-in callback ---------------------------------------------------------------------------

    [Fact]
    public async Task Callback_WithMatchingBrowserSecret_SignsIn()
    {
        var h = Create();
        await StartLogin(h);

        var result = await h.Service.HandleCallbackAsync(h.Provider.Id, "code", "s-login", Secret);

        Assert.True(result.Success);
        Assert.NotNull(result.AccessToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone-elses-secret")]
    public async Task Callback_WithMissingOrWrongBrowserSecret_IsRefusedBeforeTheCodeIsExchanged(string? secret)
    {
        var h = Create();
        await StartLogin(h);

        var result = await h.Service.HandleCallbackAsync(h.Provider.Id, "code", "s-login", secret);

        await AssertNothingHappenedAsync(h, result);
    }

    [Fact]
    public async Task Callback_AfterARefusedAttempt_TheStateIsGoneEvenForTheRightBrowser()
    {
        var h = Create();
        await StartLogin(h);
        await h.Service.HandleCallbackAsync(h.Provider.Id, "code", "s-login", "wrong");

        var result = await h.Service.HandleCallbackAsync(h.Provider.Id, "code", "s-login", Secret);

        await AssertNothingHappenedAsync(h, result);
    }

    [Fact]
    public async Task Callback_StateExpiresAfterTenMinutes()
    {
        var h = Create();
        await StartLogin(h);
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(10).AddSeconds(5);

        var result = await h.Service.HandleCallbackAsync(h.Provider.Id, "code", "s-login", Secret);

        await AssertNothingHappenedAsync(h, result);
    }

    [Fact]
    public async Task Callback_RefusesALinkState()
    {
        var h = Create();
        var user = AddUser(h.Context);
        await StartLink(h, user.Id);

        var result = await h.Service.HandleCallbackAsync(h.Provider.Id, "code", "s-link", Secret);

        Assert.False(result.Success);
        Assert.Equal(0, h.Http.Calls);
    }

    // ----- link -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Link_WithOwnStateAndBrowserSecret_Links()
    {
        var h = Create();
        var user = AddUser(h.Context);
        await StartLink(h, user.Id);

        var link = await h.Service.LinkAccountAsync(user.Id, h.Provider.Id, "code", "s-link", Secret);

        Assert.Equal(user.Id, link.UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task Link_WithMissingOrWrongBrowserSecret_IsRefused(string? secret)
    {
        var h = Create();
        var user = AddUser(h.Context);
        await StartLink(h, user.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.LinkAccountAsync(user.Id, h.Provider.Id, "code", "s-link", secret));

        Assert.Equal(0, h.Http.Calls);
        Assert.Empty(await h.Context.ExternalLogins.ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("never-issued")]
    public async Task Link_WithoutAValidState_IsRefused(string state)
    {
        var h = Create();
        var user = AddUser(h.Context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.LinkAccountAsync(user.Id, h.Provider.Id, "code", state, Secret));

        Assert.Equal(0, h.Http.Calls);
    }

    [Fact]
    public async Task Link_WithAnotherUsersState_IsRefused()
    {
        var h = Create();
        var attacker = AddUser(h.Context, "attacker@example.com");
        var victim = AddUser(h.Context, "victim@example.com");
        await StartLink(h, attacker.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.LinkAccountAsync(victim.Id, h.Provider.Id, "code", "s-link", Secret));

        Assert.Equal(0, h.Http.Calls);
        Assert.Empty(await h.Context.ExternalLogins.ToListAsync());
    }

    [Fact]
    public async Task Link_WithALoginState_IsRefused()
    {
        var h = Create();
        var user = AddUser(h.Context);
        await StartLogin(h);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.LinkAccountAsync(user.Id, h.Provider.Id, "code", "s-login", Secret));

        Assert.Equal(0, h.Http.Calls);
        Assert.Empty(await h.Context.ExternalLogins.ToListAsync());
    }

    [Fact]
    public async Task Link_StateIsSingleUse()
    {
        var h = Create();
        var user = AddUser(h.Context);
        await StartLink(h, user.Id);
        await h.Service.LinkAccountAsync(user.Id, h.Provider.Id, "code", "s-link", Secret);
        await h.Service.UnlinkAccountAsync(user.Id, "Google");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.LinkAccountAsync(user.Id, h.Provider.Id, "code", "s-link", Secret));
    }

    [Fact]
    public async Task LinkStart_WithoutAUser_IsRefused()
    {
        var h = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.GetAuthorizationUrlAsync(h.Provider.Id, RedirectUri, "s", Secret, SocialFlowPurpose.Link, null));
    }

    // ----- test doubles ---------------------------------------------------------------------------------

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var url = request.RequestUri!.ToString();
            var json = url.Contains("/token", StringComparison.Ordinal)
                ? """{"access_token":"stub-access-token","token_type":"Bearer"}"""
                : """{"sub":"5493","email":"newcomer@example.com","email_verified":true,"name":"N C","given_name":"N","family_name":"C"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(CountingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Vault : ISecretsVaultService
    {
        public Task<SecretEntry> CreateSecretAsync(string name, string plainTextValue, Guid? tenantId = null, string secretType = "Generic",
            string? description = null, string? rotationScheduleJson = null, string? tags = null, Guid? createdByUserId = null, CancellationToken ct = default)
            => Task.FromResult(new SecretEntry { Id = Guid.NewGuid(), Name = name });
        public Task<SecretEntry?> GetSecretAsync(Guid id, CancellationToken ct = default) => Task.FromResult<SecretEntry?>(null);
        public Task<string?> GetSecretValueAsync(Guid id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<List<SecretEntry>> GetSecretsAsync(Guid? tenantId = null, string? secretType = null, bool? isActive = null, CancellationToken ct = default)
            => Task.FromResult(new List<SecretEntry>());
        public Task<SecretEntry?> UpdateSecretAsync(Guid id, string? name = null, string? description = null, string? rotationScheduleJson = null,
            string? tags = null, string? secretType = null, bool? isActive = null, CancellationToken ct = default)
            => Task.FromResult<SecretEntry?>(null);
        public Task<SecretEntry> RotateSecretAsync(Guid id, string newPlainTextValue, string rotationReason = "Manual", Guid? rotatedByUserId = null,
            TimeSpan? gracePeriod = null, CancellationToken ct = default)
            => Task.FromResult(new SecretEntry { Id = id });
        public Task<List<SecretVersion>> GetSecretHistoryAsync(Guid secretId, CancellationToken ct = default) => Task.FromResult(new List<SecretVersion>());
        public Task<bool> DeleteSecretAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<SecretEntry>> GetSecretsDueForRotationAsync(CancellationToken ct = default) => Task.FromResult(new List<SecretEntry>());
    }
}
