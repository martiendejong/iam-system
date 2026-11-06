using System.IdentityModel.Tokens.Jwt;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5569: OrganizationSettings.RequireMfa used to be only an invitation hint. A member of an organization
/// with the setting on now owes the e-mailed login code on every sign-in route, even when they never switched
/// 2FA on themselves; every route asks the same question (OrganizationMfaPolicy) and issues no tokens before it
/// is answered. The refresh path cannot be used to skip the factor, and tokens carry amr=mfa afterwards.
/// </summary>
public class OrganizationRequireMfaEnforcementTests
{
    private const string Password = "Password123!";

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        }).Build();

    private sealed class Harness
    {
        public required IAMDbContext Context { get; init; }
        public required AuthService Auth { get; init; }
        public required RecordingEmailService Email { get; init; }
        public required User User { get; init; }
        public required Role Role { get; init; }

        public async Task<Tenant> AddOrganizationAsync(string slug, bool requireMfa, DateTime? roleExpiresAt = null)
        {
            var tenant = new Tenant { Name = slug, Slug = slug };
            Context.Set<Tenant>().Add(tenant);
            Context.Set<OrganizationSettings>().Add(new OrganizationSettings { TenantId = tenant.Id, RequireMfa = requireMfa });
            Context.UserRoles.Add(new UserRole { UserId = User.Id, RoleId = Role.Id, TenantId = tenant.Id, ExpiresAt = roleExpiresAt });
            await Context.SaveChangesAsync();
            return tenant;
        }

        public async Task SetRequireMfaAsync(Tenant tenant, bool value)
        {
            var settings = await Context.Set<OrganizationSettings>().FirstAsync(s => s.TenantId == tenant.Id);
            settings.RequireMfa = value;
            await Context.SaveChangesAsync();
        }
    }

    private static async Task<Harness> CreateAsync(bool ownTwoFactor = false)
    {
        var context = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var email = new RecordingEmailService();
        var role = new Role { Name = "Member" };
        var user = new User
        {
            Email = "member@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "Mem",
            LastName = "Ber",
            EmailConfirmed = true,
            IsActive = true,
            TwoFactorEnabled = ownTwoFactor,
            TwoFactorMethod = ownTwoFactor ? TwoFactorMethod.Email : TwoFactorMethod.None
        };
        context.Roles.Add(role);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return new Harness
        {
            Context = context,
            Auth = AuthServiceTestFactory.Create(context, Config(), email),
            Email = email,
            User = user,
            Role = role
        };
    }

    private static void AssertChallengedWithoutTokens(AuthResult result)
    {
        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
    }

    private static string[] AmrValues(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
            .Where(c => c.Type == OrganizationMfaPolicy.AuthenticationMethodsClaimType)
            .Select(c => c.Value).ToArray();

    // --- password route -----------------------------------------------------------------------

    [Fact]
    public async Task Password_MemberWithoutOwnTwoFactor_GetsNoTokensAndAnEmailedCode()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: true);

        var result = await h.Auth.LoginAsync(h.User.Email, Password);

        AssertChallengedWithoutTokens(result);
        Assert.Single(h.Email.SentTwoFactorCodes);
        Assert.Empty(h.Context.RefreshTokens);
    }

    [Fact]
    public async Task Password_CorrectCode_IssuesTokensCarryingAmrMfa()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: true);
        await h.Auth.LoginAsync(h.User.Email, Password);

        var verified = await h.Auth.VerifyLoginTwoFactorAsync(h.User.Id, h.Email.SentTwoFactorCodes[0].Code);

        Assert.True(verified.Success);
        Assert.True(verified.MfaCompleted);
        Assert.Equal(new[] { "mfa" }, AmrValues(verified.AccessToken!));
        Assert.True((await h.Context.RefreshTokens.SingleAsync()).MfaCompleted);
    }

    [Fact]
    public async Task Password_WrongCode_IssuesNothing()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: true);
        await h.Auth.LoginAsync(h.User.Email, Password);

        var verified = await h.Auth.VerifyLoginTwoFactorAsync(h.User.Id, "000000");

        Assert.False(verified.Success);
        Assert.Null(verified.AccessToken);
        Assert.Empty(h.Context.RefreshTokens);
    }

    [Fact]
    public async Task VerifyCode_ForUserOutsideAnyRequireMfaOrganization_IsRefused()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: false);

        var verified = await h.Auth.VerifyLoginTwoFactorAsync(h.User.Id, "123456");

        Assert.False(verified.Success);
        Assert.Null(verified.AccessToken);
    }

    // --- every other sign-in route shares one gate (magic link, e-mail/SMS code, passkey, social) ------

    [Fact]
    public async Task Passwordless_MemberWithoutOwnTwoFactor_GetsNoTokens()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: true);

        var result = await h.Auth.CompletePasswordlessLoginAsync(h.User);

        AssertChallengedWithoutTokens(result);
        Assert.Single(h.Email.SentTwoFactorCodes);
        Assert.Empty(h.Context.RefreshTokens);
    }

    [Fact]
    public async Task Passwordless_CodeStepCompletesTheSignIn()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: true);
        await h.Auth.CompletePasswordlessLoginAsync(h.User);

        var verified = await h.Auth.VerifyLoginTwoFactorAsync(h.User.Id, h.Email.SentTwoFactorCodes[0].Code);

        Assert.True(verified.Success);
        Assert.Equal(new[] { "mfa" }, AmrValues(verified.AccessToken!));
    }

    [Fact]
    public async Task PublicLoginBypass_MemberOfRequireMfaOrganization_DoesNotIssueTokens()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: true);

        var result = await h.Auth.LoginBypassPasswordAsync(h.User);

        AssertChallengedWithoutTokens(result);
        Assert.Empty(h.Context.RefreshTokens);
    }

    // --- scope of the rule ---------------------------------------------------------------------

    [Fact]
    public async Task UserOutsideRequireMfaOrganizations_IsUnaffected_OnEveryRoute()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("relaxed", requireMfa: false);

        var password = await h.Auth.LoginAsync(h.User.Email, Password);
        var passwordless = await h.Auth.CompletePasswordlessLoginAsync(h.User);

        Assert.False(password.RequiresTwoFactor);
        Assert.NotNull(password.AccessToken);
        Assert.Empty(AmrValues(password.AccessToken!));
        Assert.False(passwordless.RequiresTwoFactor);
        Assert.NotNull(passwordless.AccessToken);
        Assert.Empty(h.Email.SentTwoFactorCodes);
    }

    [Fact]
    public async Task TurningTheSettingOff_RemovesTheRequirement()
    {
        var h = await CreateAsync();
        var org = await h.AddOrganizationAsync("acme", requireMfa: true);
        AssertChallengedWithoutTokens(await h.Auth.LoginAsync(h.User.Email, Password));

        await h.SetRequireMfaAsync(org, false);
        var result = await h.Auth.LoginAsync(h.User.Email, Password);

        Assert.False(result.RequiresTwoFactor);
        Assert.NotNull(result.AccessToken);
    }

    [Fact]
    public async Task UserInSeveralOrganizations_IsHeldToTheStrictestOne()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("relaxed", requireMfa: false);
        await h.AddOrganizationAsync("strict", requireMfa: true);

        AssertChallengedWithoutTokens(await h.Auth.LoginAsync(h.User.Email, Password));
    }

    [Fact]
    public async Task ExpiredMembership_DoesNotCount()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("former", requireMfa: true, roleExpiresAt: DateTime.UtcNow.AddDays(-1));

        var result = await h.Auth.LoginAsync(h.User.Email, Password);

        Assert.False(result.RequiresTwoFactor);
        Assert.NotNull(result.AccessToken);
    }

    [Fact]
    public async Task MemberWhoNeverSetUpASecondFactor_CanStillSignIn_ThroughTheEmailedCode()
    {
        var h = await CreateAsync(ownTwoFactor: false);
        await h.AddOrganizationAsync("acme", requireMfa: true);

        await h.Auth.LoginAsync(h.User.Email, Password);
        var verified = await h.Auth.VerifyLoginTwoFactorAsync(h.User.Id, h.Email.SentTwoFactorCodes[0].Code);

        Assert.True(verified.Success); // no enrolment step, nobody is locked out
        Assert.False(h.User.TwoFactorEnabled);
    }

    // --- refresh path -------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_SessionWithoutSecondFactor_IsRefusedOnceTheOrganizationRequiresMfa()
    {
        var h = await CreateAsync();
        var org = await h.AddOrganizationAsync("acme", requireMfa: false);
        var login = await h.Auth.LoginAsync(h.User.Email, Password); // signed in before the setting existed
        await h.SetRequireMfaAsync(org, true);

        var refreshed = await h.Auth.RefreshTokenAsync(login.RefreshToken!);

        Assert.False(refreshed.Success);
        Assert.Null(refreshed.AccessToken);
        Assert.Null(refreshed.RefreshToken);
    }

    [Fact]
    public async Task Refresh_SessionThatCompletedTheFactor_KeepsRefreshingAndKeepsTheClaim()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: true);
        await h.Auth.LoginAsync(h.User.Email, Password);
        var verified = await h.Auth.VerifyLoginTwoFactorAsync(h.User.Id, h.Email.SentTwoFactorCodes[0].Code);

        var first = await h.Auth.RefreshTokenAsync(verified.RefreshToken!);
        var second = await h.Auth.RefreshTokenAsync(first.RefreshToken!);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(new[] { "mfa" }, AmrValues(second.AccessToken!));
    }

    [Fact]
    public async Task Refresh_SessionWithoutSecondFactor_StillWorksOutsideRequireMfaOrganizations()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("relaxed", requireMfa: false);
        var login = await h.Auth.LoginAsync(h.User.Email, Password);

        var refreshed = await h.Auth.RefreshTokenAsync(login.RefreshToken!);

        Assert.True(refreshed.Success);
    }

    // --- the shared policy itself ----------------------------------------------------------------

    [Fact]
    public async Task Policy_ReportsRequirementPerUser()
    {
        var h = await CreateAsync();
        await h.AddOrganizationAsync("acme", requireMfa: true);
        var outsider = new User { Email = "outsider@example.com", PasswordHash = "x", FirstName = "O", LastName = "U" };
        h.Context.Users.Add(outsider);
        await h.Context.SaveChangesAsync();

        Assert.True(await OrganizationMfaPolicy.IsMfaRequiredAsync(h.Context, h.User.Id));
        Assert.False(await OrganizationMfaPolicy.IsMfaRequiredAsync(h.Context, outsider.Id));
    }
}
