using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4707: a social login with no linked identity was matched to the existing account with the same e-mail
/// address without checking the provider verified it (an attacker's own Entra tenant can set mail/userPrincipalName
/// to any address), then linked permanently and signed in. These tests drive the real SocialAuthService (in-memory
/// DB, stubbed provider HTTP) and confirm: an existing account is matched only for an e-mail the provider verifies
/// (Google email_verified, GitHub verified primary, Apple email_verified claim), Microsoft never matches by e-mail,
/// a refusal writes nothing, already linked identities and explicit linking still work, and auto-created users get
/// EmailConfirmed only for verified addresses.
/// Task 4765 (pre-hijack): a verified provider e-mail is also matched only to an account whose own e-mail is
/// confirmed. An attacker can hold an account for the victim's address without proof (unverified Microsoft
/// auto-create, or a password registration the owner never confirmed); linking the real owner's verified Google or
/// GitHub login to it would leave the attacker signed in to the owner's account.
/// </summary>
public class SocialLoginVerifiedEmailTests
{
    private const string RedirectUri = "https://app.example.com/callback";
    private const string Email = "victim@example.com";
    private const string ProviderUserId = "4707";
    private const string AppleClientId = "apple-client-4707";
    private static readonly DateTime OldTimestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // One RSA key for every Apple test: the service caches Apple's JWKS in a static field.
    private static readonly RSA AppleKey = RSA.Create(2048);

    private sealed class Harness
    {
        public required DbContextOptions<IAMDbContext> Options { get; init; }
        public required IAMDbContext Context { get; init; }
        public required SocialAuthService Service { get; init; }
        public required IdentityProvider Provider { get; init; }
        public required ProviderStub Stub { get; init; }

        public IAMDbContext NewReadContext() => new(Options);

        public async Task<AuthResult> CallbackAsync(string state = "state-1")
        {
            await Service.GetAuthorizationUrlAsync(Provider.Id, RedirectUri, state);
            return await Service.HandleCallbackAsync(Provider.Id, "auth-code", state);
        }
    }

    // sameDatabaseAs: a second provider on the database of an earlier harness (task 4765: two different providers
    // meeting on one account).
    private static Harness CreateHarness(IdentityProviderType type, ProviderStub stub, bool autoCreate = false, string? attributeMapping = null,
        Harness? sameDatabaseAs = null)
    {
        var options = sameDatabaseAs?.Options ?? new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new IAMDbContext(options);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5",
            ["SocialAuth:RedirectUri"] = RedirectUri,
            ["Apple:ClientId"] = AppleClientId
        }).Build();

        var provider = new IdentityProvider
        {
            Name = $"test-{type}",
            DisplayName = type.ToString(),
            Type = type,
            ClientId = "test-client-id",
            ClientSecret = "test-secret",
            IsActive = true,
            AutoCreateUsers = autoCreate,
            AttributeMapping = attributeMapping
        };
        context.IdentityProviders.Add(provider);
        context.SaveChanges();

        var service = new SocialAuthService(
            context,
            config,
            new StubFactory(stub),
            new StubVault(),
            new MemoryCache(new MemoryCacheOptions()),
            AuthServiceTestFactory.Create(context, config),
            NullLogger<SocialAuthService>.Instance);

        return new Harness { Options = options, Context = context, Service = service, Provider = provider, Stub = stub };
    }

    private static User AddUser(IAMDbContext context, bool emailConfirmed = true)
    {
        var user = new User
        {
            Email = Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            FirstName = "Vic",
            LastName = "Tim",
            EmailConfirmed = emailConfirmed,
            IsActive = true,
            LastLoginAt = OldTimestamp
        };
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    private static async Task AssertRefusedWithNothingWrittenAsync(Harness h, AuthResult result, User victim, string expectedMessagePart)
    {
        Assert.False(result.Success);
        Assert.Contains(expectedMessagePart, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.User);

        using var read = h.NewReadContext();
        Assert.Empty(await read.ExternalLogins.ToListAsync());
        Assert.Empty(await read.RefreshTokens.ToListAsync());
        var users = await read.Users.ToListAsync();
        Assert.Single(users); // no second account either
        Assert.Equal(victim.Id, users[0].Id);
        Assert.Equal(OldTimestamp, users[0].LastLoginAt);
    }

    // ----- Google -------------------------------------------------------------------------------------

    [Fact]
    public async Task Google_VerifiedEmail_LinksTheExistingAccountAndSignsIn()
    {
        var h = CreateHarness(IdentityProviderType.Google, new ProviderStub { GoogleEmailVerified = "true" });
        var user = AddUser(h.Context);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.Equal(user.Id, result.User!.Id);
        using var read = h.NewReadContext();
        var link = await read.ExternalLogins.SingleAsync();
        Assert.Equal(user.Id, link.UserId);
        Assert.Equal("Google", link.Provider);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"false\"")]
    [InlineData(null)]       // claim missing: never defaulted to verified
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public async Task Google_UnverifiedOrMissingFlag_IsRefused_AndCreatesNoLink(string? emailVerifiedJson)
    {
        var h = CreateHarness(IdentityProviderType.Google, new ProviderStub { GoogleEmailVerified = emailVerifiedJson }, autoCreate: true);
        var victim = AddUser(h.Context);

        var result = await h.CallbackAsync();

        await AssertRefusedWithNothingWrittenAsync(h, result, victim, "not verified");
    }

    [Fact]
    public async Task Google_VerifiedFlagAsTheStringTrue_CountsAsVerified()
    {
        var h = CreateHarness(IdentityProviderType.Google, new ProviderStub { GoogleEmailVerified = "\"true\"" });
        AddUser(h.Context);

        Assert.True((await h.CallbackAsync()).Success);
    }

    [Fact]
    public async Task Google_VerifiedFlagDoesNotVouchForAnEmailMappedFromAnotherClaim()
    {
        var h = CreateHarness(IdentityProviderType.Google, new ProviderStub { GoogleEmailVerified = "true", GoogleAltEmail = Email },
            attributeMapping: "{\"email\":\"alt_email\"}");
        var victim = AddUser(h.Context);

        var result = await h.CallbackAsync();

        await AssertRefusedWithNothingWrittenAsync(h, result, victim, "not verified");
    }

    // ----- GitHub -------------------------------------------------------------------------------------

    [Fact]
    public async Task GitHub_VerifiedPrimaryEmail_LinksAndSignsIn()
    {
        var h = CreateHarness(IdentityProviderType.GitHub, new ProviderStub { GitHubEmails = Emails(Email, primary: true, verified: true) });
        var user = AddUser(h.Context);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        using var read = h.NewReadContext();
        Assert.Equal(user.Id, (await read.ExternalLogins.SingleAsync()).UserId);
    }

    [Fact]
    public async Task GitHub_UnverifiedPrimaryEmail_IsRefused_EvenWhenThePublicProfileShowsTheSameAddress()
    {
        var h = CreateHarness(IdentityProviderType.GitHub,
            new ProviderStub { GitHubProfileEmail = Email, GitHubEmails = Emails(Email, primary: true, verified: false) }, autoCreate: true);
        var victim = AddUser(h.Context);

        var result = await h.CallbackAsync();

        await AssertRefusedWithNothingWrittenAsync(h, result, victim, "not verified");
    }

    [Fact]
    public async Task GitHub_VerifiedAddressThatIsNotThePrimary_DoesNotCount()
    {
        var emails = $$"""[{"email":"other@example.com","primary":true,"verified":true},{"email":"{{Email}}","primary":false,"verified":true}]""";
        var h = CreateHarness(IdentityProviderType.GitHub, new ProviderStub { GitHubProfileEmail = Email, GitHubEmails = emails });
        var victim = AddUser(h.Context);

        var result = await h.CallbackAsync();

        // The primary is other@example.com (verified, no account): nothing matches, auto-create is off.
        Assert.False(result.Success);
        using var read = h.NewReadContext();
        Assert.Empty(await read.ExternalLogins.ToListAsync());
        Assert.Equal(OldTimestamp, (await read.Users.SingleAsync(u => u.Id == victim.Id)).LastLoginAt);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task GitHub_EmailsEndpointUnavailable_ProfileEmailIsUnverified_AndRefused(int status)
    {
        var h = CreateHarness(IdentityProviderType.GitHub,
            new ProviderStub { GitHubProfileEmail = Email, GitHubEmails = "{}", GitHubEmailsStatus = (HttpStatusCode)status });
        var victim = AddUser(h.Context);

        var result = await h.CallbackAsync();

        await AssertRefusedWithNothingWrittenAsync(h, result, victim, "not verified");
    }

    // ----- Microsoft ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("mail")]
    [InlineData("userPrincipalName")]
    public async Task Microsoft_EmailMatchingAnExistingUserWithoutALink_IsRefusedWithAClearMessage(string field)
    {
        var h = CreateHarness(IdentityProviderType.Microsoft, new ProviderStub { MicrosoftEmailField = field }, autoCreate: true);
        var victim = AddUser(h.Context);

        var result = await h.CallbackAsync();

        await AssertRefusedWithNothingWrittenAsync(h, result, victim, "Microsoft sign-in cannot be matched");
        Assert.Contains("link your Microsoft account", result.Error);
    }

    [Fact]
    public async Task Microsoft_AlreadyLinkedIdentity_StillSignsIn()
    {
        var h = CreateHarness(IdentityProviderType.Microsoft, new ProviderStub());
        var user = AddUser(h.Context);
        h.Context.ExternalLogins.Add(new ExternalLogin
        {
            UserId = user.Id, Provider = "Microsoft", ProviderUserId = ProviderUserId, Email = Email, LastUsedAt = OldTimestamp
        });
        h.Context.SaveChanges();

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.Equal(user.Id, result.User!.Id);
    }

    [Fact]
    public async Task Microsoft_UnknownEmail_AutoCreatesAnAccountThatIsNotEmailConfirmed()
    {
        var h = CreateHarness(IdentityProviderType.Microsoft, new ProviderStub(), autoCreate: true);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        using var read = h.NewReadContext();
        var created = await read.Users.SingleAsync();
        Assert.Equal(Email, created.Email);
        Assert.False(created.EmailConfirmed);
    }

    // ----- Apple --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public async Task Apple_MatchesAnExistingAccountOnlyForAVerifiedEmailClaim(string? emailVerifiedClaim, bool expectLinked)
    {
        var h = CreateHarness(IdentityProviderType.Apple, new ProviderStub { AppleIdToken = AppleToken(Email, emailVerifiedClaim) });
        var victim = AddUser(h.Context);

        var result = await h.CallbackAsync();

        if (expectLinked)
        {
            Assert.True(result.Success);
            using var read = h.NewReadContext();
            Assert.Equal(victim.Id, (await read.ExternalLogins.SingleAsync()).UserId);
        }
        else
        {
            await AssertRefusedWithNothingWrittenAsync(h, result, victim, "not verified");
        }
    }

    // ----- auto-created users -------------------------------------------------------------------------

    [Theory]
    [InlineData(IdentityProviderType.Google, true)]
    [InlineData(IdentityProviderType.Google, false)]
    [InlineData(IdentityProviderType.GitHub, true)]
    [InlineData(IdentityProviderType.GitHub, false)]
    public async Task AutoCreatedUser_IsEmailConfirmedOnlyWhenTheProviderVerifiedTheAddress(IdentityProviderType type, bool verified)
    {
        var stub = type == IdentityProviderType.Google
            ? new ProviderStub { GoogleEmailVerified = verified ? "true" : "false" }
            : new ProviderStub { GitHubEmails = Emails(Email, primary: true, verified: verified) };
        var h = CreateHarness(type, stub, autoCreate: true);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        using var read = h.NewReadContext();
        var created = await read.Users.SingleAsync();
        Assert.Equal(Email, created.Email);
        Assert.Equal(verified, created.EmailConfirmed);
    }

    [Fact]
    public async Task AutoCreatedUser_WithoutAnEmail_GetsAPlaceholderAndIsNotConfirmed()
    {
        var h = CreateHarness(IdentityProviderType.Google, new ProviderStub { GoogleHasEmail = false }, autoCreate: true);

        Assert.True((await h.CallbackAsync()).Success);

        using var read = h.NewReadContext();
        var created = await read.Users.SingleAsync();
        Assert.EndsWith("@google.external", created.Email);
        Assert.False(created.EmailConfirmed);
    }

    [Fact]
    public async Task ExistingLink_SignsInRegardlessOfTheEmailVerifiedFlag()
    {
        var h = CreateHarness(IdentityProviderType.Google, new ProviderStub { GoogleEmailVerified = "false" });
        var user = AddUser(h.Context);
        h.Context.ExternalLogins.Add(new ExternalLogin
        {
            UserId = user.Id, Provider = "Google", ProviderUserId = ProviderUserId, Email = Email, LastUsedAt = OldTimestamp
        });
        h.Context.SaveChanges();

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.Equal(user.Id, result.User!.Id);
    }

    // ----- explicit linking while logged in -------------------------------------------------------------

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.Microsoft)]
    [InlineData(IdentityProviderType.GitHub)]
    public async Task ExplicitLinking_StillWorksForEveryProvider_EvenWithAnUnverifiedEmail(IdentityProviderType type)
    {
        var stub = type switch
        {
            IdentityProviderType.Google => new ProviderStub { GoogleEmailVerified = "false" },
            IdentityProviderType.GitHub => new ProviderStub { GitHubEmails = Emails(Email, primary: true, verified: false) },
            _ => new ProviderStub()
        };
        var h = CreateHarness(type, stub);
        var user = AddUser(h.Context);

        var link = await h.Service.LinkAccountAsync(user.Id, h.Provider.Id, "auth-code");

        Assert.Equal(user.Id, link.UserId);
        Assert.Equal(type.ToString(), link.Provider);
        using var read = h.NewReadContext();
        Assert.Equal(ProviderUserId, (await read.ExternalLogins.SingleAsync()).ProviderUserId);
    }

    // ----- task 4765: a verified e-mail only meets an account whose own e-mail is confirmed --------------

    [Fact]
    public async Task PreHijack_UnverifiedMicrosoftAutoCreate_ThenVerifiedGoogleLoginWithTheSameEmail_IsRefused()
    {
        // The attacker signs in with Microsoft using an address of their own Entra tenant that reads victim@example.com:
        // the account is auto-created, holds the victim's address and keeps its Microsoft link, but is not confirmed.
        var microsoft = CreateHarness(IdentityProviderType.Microsoft, new ProviderStub(), autoCreate: true);
        Assert.True((await microsoft.CallbackAsync()).Success);
        Guid attackerAccountId;
        DateTime? lastLoginAfterAttackerSignIn;
        using (var read = microsoft.NewReadContext())
        {
            var attackerAccount = await read.Users.SingleAsync();
            Assert.False(attackerAccount.EmailConfirmed);
            attackerAccountId = attackerAccount.Id;
            lastLoginAfterAttackerSignIn = attackerAccount.LastLoginAt;
        }

        // The real owner now signs in with Google, which verified victim@example.com.
        var google = CreateHarness(IdentityProviderType.Google, new ProviderStub { GoogleEmailVerified = "true" }, autoCreate: true,
            sameDatabaseAs: microsoft);
        var result = await google.CallbackAsync();

        Assert.False(result.Success);
        Assert.Contains("not been confirmed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.User);

        using var after = google.NewReadContext();
        var link = await after.ExternalLogins.SingleAsync(); // still only the attacker's Microsoft link
        Assert.Equal("Microsoft", link.Provider);
        Assert.Equal(attackerAccountId, link.UserId);
        var account = await after.Users.SingleAsync(); // no second account, nothing changed on the first
        Assert.Equal(attackerAccountId, account.Id);
        Assert.False(account.EmailConfirmed);
        Assert.Equal(lastLoginAfterAttackerSignIn, account.LastLoginAt);
        Assert.Single(await after.RefreshTokens.ToListAsync()); // only the attacker's own sign-in issued one
    }

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.GitHub)]
    [InlineData(IdentityProviderType.Apple)]
    public async Task VerifiedEmail_MatchingAnAccountThatIsNotEmailConfirmed_IsRefused_BeforeAnythingIsWritten(IdentityProviderType type)
    {
        // An account registered with a password for the victim's address that was never confirmed has the same shape.
        var h = CreateHarness(type, VerifiedStub(type), autoCreate: true);
        var unconfirmed = AddUser(h.Context, emailConfirmed: false);

        var result = await h.CallbackAsync();

        await AssertRefusedWithNothingWrittenAsync(h, result, unconfirmed, "not been confirmed");
        Assert.Contains(type.ToString(), result.Error);
        using var read = h.NewReadContext();
        Assert.False((await read.Users.SingleAsync()).EmailConfirmed); // the login does not confirm the address either
    }

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.GitHub)]
    [InlineData(IdentityProviderType.Apple)]
    public async Task VerifiedEmail_MatchingAConfirmedAccount_StillLinksAndSignsIn(IdentityProviderType type)
    {
        var h = CreateHarness(type, VerifiedStub(type), autoCreate: true);
        var user = AddUser(h.Context, emailConfirmed: true);

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.Equal(user.Id, result.User!.Id);
        Assert.NotNull(result.AccessToken);
        using var read = h.NewReadContext();
        var link = await read.ExternalLogins.SingleAsync();
        Assert.Equal(user.Id, link.UserId);
        Assert.Equal(type.ToString(), link.Provider);
        Assert.Single(await read.Users.ToListAsync());
    }

    [Fact]
    public async Task VerifiedEmail_MatchesTheAccountOnceItsOwnerConfirmedTheAddress()
    {
        var h = CreateHarness(IdentityProviderType.Google, new ProviderStub { GoogleEmailVerified = "true" });
        var user = AddUser(h.Context, emailConfirmed: false);
        Assert.False((await h.CallbackAsync("state-refused")).Success);

        user.EmailConfirmed = true; // the owner followed the verification link
        h.Context.SaveChanges();
        var result = await h.CallbackAsync("state-confirmed");

        Assert.True(result.Success);
        Assert.Equal(user.Id, result.User!.Id);
    }

    [Fact]
    public async Task UnconfirmedAccount_StillSignsInThroughItsOwnLinkedIdentity()
    {
        // Unverified Microsoft accounts keep working as they do today; only a different provider's e-mail match is refused.
        var h = CreateHarness(IdentityProviderType.Microsoft, new ProviderStub());
        var user = AddUser(h.Context, emailConfirmed: false);
        h.Context.ExternalLogins.Add(new ExternalLogin
        {
            UserId = user.Id, Provider = "Microsoft", ProviderUserId = ProviderUserId, Email = Email, LastUsedAt = OldTimestamp
        });
        h.Context.SaveChanges();

        var result = await h.CallbackAsync();

        Assert.True(result.Success);
        Assert.Equal(user.Id, result.User!.Id);
    }

    [Theory]
    [InlineData(IdentityProviderType.Google)]
    [InlineData(IdentityProviderType.Microsoft)]
    [InlineData(IdentityProviderType.GitHub)]
    public async Task ExplicitLinking_StillWorksForAnAccountThatIsNotEmailConfirmed(IdentityProviderType type)
    {
        // The logged-in user asks for the link themselves; no e-mail match is involved.
        var h = CreateHarness(type, type == IdentityProviderType.Microsoft ? new ProviderStub() : VerifiedStub(type));
        var user = AddUser(h.Context, emailConfirmed: false);

        var link = await h.Service.LinkAccountAsync(user.Id, h.Provider.Id, "auth-code");

        Assert.Equal(user.Id, link.UserId);
        using var read = h.NewReadContext();
        Assert.Equal(type.ToString(), (await read.ExternalLogins.SingleAsync()).Provider);
    }

    // ----- test doubles ---------------------------------------------------------------------------------

    private static ProviderStub VerifiedStub(IdentityProviderType type) => type switch
    {
        IdentityProviderType.Google => new ProviderStub { GoogleEmailVerified = "true" },
        IdentityProviderType.GitHub => new ProviderStub { GitHubEmails = Emails(Email, primary: true, verified: true) },
        IdentityProviderType.Apple => new ProviderStub { AppleIdToken = AppleToken(Email, "true") },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "no verified e-mail stub")
    };

    private static string Emails(string address, bool primary, bool verified) =>
        $$"""[{"email":"{{address}}","primary":{{primary.ToString().ToLowerInvariant()}},"verified":{{verified.ToString().ToLowerInvariant()}}}]""";

    private static string AppleToken(string email, string? emailVerifiedClaim)
    {
        var claims = new List<Claim> { new("sub", ProviderUserId), new("email", email) };
        if (emailVerifiedClaim != null)
            claims.Add(new Claim("email_verified", emailVerifiedClaim));

        var token = new JwtSecurityToken(
            issuer: "https://appleid.apple.com",
            audience: AppleClientId,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(AppleKey) { KeyId = "apple-test-key" }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string AppleJwks()
    {
        var p = AppleKey.ExportParameters(false);
        return $$"""{"keys":[{"kty":"RSA","kid":"apple-test-key","use":"sig","alg":"RS256","n":"{{Base64UrlEncoder.Encode(p.Modulus!)}}","e":"{{Base64UrlEncoder.Encode(p.Exponent!)}}"}]}""";
    }

    private sealed class ProviderStub
    {
        /// <summary>JSON literal for Google's email_verified (null = claim absent).</summary>
        public string? GoogleEmailVerified { get; init; } = "true";
        public bool GoogleHasEmail { get; init; } = true;
        public string? GoogleAltEmail { get; init; }
        public string MicrosoftEmailField { get; init; } = "mail";
        public string? GitHubProfileEmail { get; init; }
        public string GitHubEmails { get; init; } = "[]";
        public HttpStatusCode GitHubEmailsStatus { get; init; } = HttpStatusCode.OK;
        public string? AppleIdToken { get; init; }
    }

    private sealed class StubFactory(ProviderStub stub) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(stub), disposeHandler: false);
    }

    private sealed class StubHandler(ProviderStub stub) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var status = HttpStatusCode.OK;
            string json;

            if (url.Contains("appleid.apple.com/auth/keys", StringComparison.Ordinal))
            {
                json = AppleJwks();
            }
            else if (url.Contains("/token", StringComparison.Ordinal) || url.Contains("access_token", StringComparison.Ordinal))
            {
                json = stub.AppleIdToken != null
                    ? $$"""{"access_token":"stub-access-token","token_type":"Bearer","id_token":"{{stub.AppleIdToken}}"}"""
                    : """{"access_token":"stub-access-token","token_type":"Bearer"}""";
            }
            else if (url.Contains("/user/emails", StringComparison.Ordinal))
            {
                status = stub.GitHubEmailsStatus;
                json = stub.GitHubEmails;
            }
            else if (url.Contains("graph.microsoft.com", StringComparison.Ordinal))
            {
                json = $$"""{"id":"{{ProviderUserId}}","{{stub.MicrosoftEmailField}}":"{{Email}}","displayName":"Vic Tim","givenName":"Vic","surname":"Tim"}""";
            }
            else if (url.Contains("api.github.com/user", StringComparison.Ordinal))
            {
                var email = stub.GitHubProfileEmail != null ? $"\"email\":\"{stub.GitHubProfileEmail}\"," : "";
                json = $$"""{"id":{{ProviderUserId}},{{email}}"name":"Vic Tim","login":"victim"}""";
            }
            else
            {
                var emailPart = stub.GoogleHasEmail ? $"\"email\":\"{Email}\"," : "";
                var verifiedPart = stub.GoogleEmailVerified != null ? $"\"email_verified\":{stub.GoogleEmailVerified}," : "";
                var altPart = stub.GoogleAltEmail != null ? $"\"alt_email\":\"{stub.GoogleAltEmail}\"," : "";
                json = $$"""{"sub":"{{ProviderUserId}}",{{emailPart}}{{verifiedPart}}{{altPart}}"name":"Vic Tim","given_name":"Vic","family_name":"Tim"}""";
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class StubVault : ISecretsVaultService
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
