using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IAM.API.Tests.Services;

/// <summary>Task 5566: the e-mailed 2FA step (verify, resend) only works with the challenge token a first step issued.</summary>
public class TwoFactorChallengeTokenTests
{
    private const string Password = "Password123!";

    private sealed record Handle(
        IAuthService Auth, IAMDbContext Context, RecordingEmailService Email, ManualTimeProvider Time);

    private static Handle Create()
    {
        var context = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        }).Build();
        var email = new RecordingEmailService();
        var time = new ManualTimeProvider();
        var auth = AuthServiceTestFactory.Create(context, config, email,
            challengeService: AuthServiceTestFactory.CreateChallengeService(time));
        return new Handle(auth, context, email, time);
    }

    private static async Task<User> AddUserAsync(IAMDbContext context, string name)
    {
        var user = new User
        {
            Email = $"{name}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = name,
            LastName = "Tester",
            EmailConfirmed = true,
            IsActive = true,
            TwoFactorEnabled = true,
            TwoFactorMethod = TwoFactorMethod.Email
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Verify_WithoutToken_NeverLogsInNorCountsAFailure()
    {
        var h = Create();
        var user = await AddUserAsync(h.Context, "alice");
        var login = await h.Auth.LoginAsync(user.Email, Password);
        var code = h.Email.SentTwoFactorCodes[0].Code;

        foreach (var token in new string?[] { null, "", "not-a-token" })
        {
            // even the CORRECT code is useless without the token
            var r = await h.Auth.VerifyLoginTwoFactorAsync(user.Id, token, code);
            Assert.False(r.Success);
            Assert.Null(r.AccessToken);
        }

        await h.Context.Entry(user).ReloadAsync();
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.False(user.IsLockedOut);
        // ...and the real flow is untouched
        Assert.True((await h.Auth.VerifyLoginTwoFactorAsync(user.Id, login.TwoFactorChallengeToken, code)).Success);
    }

    [Fact]
    public async Task Resend_WithoutOrWithWrongToken_SendsNoMail()
    {
        var h = Create();
        var user = await AddUserAsync(h.Context, "alice");
        var other = await AddUserAsync(h.Context, "bob");
        await h.Auth.LoginAsync(user.Email, Password);
        var bobLogin = await h.Auth.LoginAsync(other.Email, Password);
        var before = h.Email.SentTwoFactorCodes.Count;

        await h.Auth.ResendLoginTwoFactorCodeAsync(user.Id, null);
        await h.Auth.ResendLoginTwoFactorCodeAsync(user.Id, "garbage");
        await h.Auth.ResendLoginTwoFactorCodeAsync(user.Id, bobLogin.TwoFactorChallengeToken);

        Assert.Equal(before, h.Email.SentTwoFactorCodes.Count);
    }

    [Fact]
    public async Task Resend_WithValidToken_SendsAnotherCode_AndKeepsTokenUsable()
    {
        var h = Create();
        var user = await AddUserAsync(h.Context, "alice");
        var login = await h.Auth.LoginAsync(user.Email, Password);

        await h.Auth.ResendLoginTwoFactorCodeAsync(user.Id, login.TwoFactorChallengeToken);

        Assert.Equal(2, h.Email.SentTwoFactorCodes.Count);
        Assert.True((await h.Auth.VerifyLoginTwoFactorAsync(user.Id, login.TwoFactorChallengeToken, h.Email.SentTwoFactorCodes[1].Code)).Success);
    }

    [Fact]
    public async Task TokenForUserA_CannotBeUsedForUserB()
    {
        var h = Create();
        var a = await AddUserAsync(h.Context, "alice");
        var b = await AddUserAsync(h.Context, "bob");
        var loginA = await h.Auth.LoginAsync(a.Email, Password);
        await h.Auth.LoginAsync(b.Email, Password);
        var codeB = h.Email.SentTwoFactorCodes[1].Code;

        var r = await h.Auth.VerifyLoginTwoFactorAsync(b.Id, loginA.TwoFactorChallengeToken, codeB);

        Assert.False(r.Success);
        await h.Context.Entry(b).ReloadAsync();
        Assert.Equal(0, b.FailedLoginAttempts);
    }

    [Fact]
    public async Task Token_IsRejectedAfterExpiry()
    {
        var h = Create();
        var user = await AddUserAsync(h.Context, "alice");
        var login = await h.Auth.LoginAsync(user.Email, Password);
        var code = h.Email.SentTwoFactorCodes[0].Code;

        h.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.False((await h.Auth.VerifyLoginTwoFactorAsync(user.Id, login.TwoFactorChallengeToken, code)).Success);
        var before = h.Email.SentTwoFactorCodes.Count;
        await h.Auth.ResendLoginTwoFactorCodeAsync(user.Id, login.TwoFactorChallengeToken);
        Assert.Equal(before, h.Email.SentTwoFactorCodes.Count);
    }

    [Fact]
    public async Task Token_IsRejectedAfterOneSuccessfulUse()
    {
        var h = Create();
        var user = await AddUserAsync(h.Context, "alice");
        var login = await h.Auth.LoginAsync(user.Email, Password);
        var code = h.Email.SentTwoFactorCodes[0].Code;
        Assert.True((await h.Auth.VerifyLoginTwoFactorAsync(user.Id, login.TwoFactorChallengeToken, code)).Success);

        // a fresh code for the same user, but the spent token must not work
        await h.Auth.LoginAsync(user.Email, Password);
        var code2 = h.Email.SentTwoFactorCodes[1].Code;
        Assert.False((await h.Auth.VerifyLoginTwoFactorAsync(user.Id, login.TwoFactorChallengeToken, code2)).Success);
        var before = h.Email.SentTwoFactorCodes.Count;
        await h.Auth.ResendLoginTwoFactorCodeAsync(user.Id, login.TwoFactorChallengeToken);
        Assert.Equal(before, h.Email.SentTwoFactorCodes.Count);
    }

    [Fact]
    public async Task Stranger_GuessingWrongCodesForSomeonesUserId_CannotLockThemOut()
    {
        var h = Create();
        var victim = await AddUserAsync(h.Context, "alice");
        var login = await h.Auth.LoginAsync(victim.Email, Password);

        for (var i = 0; i < 20; i++)
            await h.Auth.VerifyLoginTwoFactorAsync(victim.Id, null, "000000");

        await h.Context.Entry(victim).ReloadAsync();
        Assert.False(victim.IsLockedOut);
        Assert.True((await h.Auth.VerifyLoginTwoFactorAsync(victim.Id, login.TwoFactorChallengeToken, h.Email.SentTwoFactorCodes[0].Code)).Success);
    }

    [Fact]
    public async Task WrongCode_WithValidToken_StillCountsAndTokenSurvives()
    {
        var h = Create();
        var user = await AddUserAsync(h.Context, "alice");
        var login = await h.Auth.LoginAsync(user.Email, Password);

        Assert.False((await h.Auth.VerifyLoginTwoFactorAsync(user.Id, login.TwoFactorChallengeToken, "000000")).Success);
        await h.Context.Entry(user).ReloadAsync();
        Assert.Equal(1, user.FailedLoginAttempts);
        Assert.True((await h.Auth.VerifyLoginTwoFactorAsync(user.Id, login.TwoFactorChallengeToken, h.Email.SentTwoFactorCodes[0].Code)).Success);
    }

    [Fact]
    public async Task EveryFirstStep_ThatReturnsRequiresTwoFactor_IssuesAToken()
    {
        var h = Create();
        var user = await AddUserAsync(h.Context, "alice");

        var password = await h.Auth.LoginAsync(user.Email, Password);
        var passwordless = await h.Auth.CompletePasswordlessLoginAsync(user);

        Assert.True(password.RequiresTwoFactor);
        Assert.False(string.IsNullOrEmpty(password.TwoFactorChallengeToken));
        Assert.True(passwordless.RequiresTwoFactor);
        Assert.False(string.IsNullOrEmpty(passwordless.TwoFactorChallengeToken));
        Assert.NotEqual(password.TwoFactorChallengeToken, passwordless.TwoFactorChallengeToken);
    }
}
