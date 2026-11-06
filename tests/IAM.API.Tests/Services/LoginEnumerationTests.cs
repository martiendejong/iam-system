using System.Diagnostics;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5166: a failed password sign-in must look the same whether the email is unknown, the
/// password is wrong or the account is locked (same message, same work), password failures must no
/// longer lock the real user, and only the 2FA step's lock (task 4523) may still block a sign-in -
/// after a correct password and without a timestamp.
/// </summary>
public class LoginEnumerationTests
{
    private const string Password = "Correct-Horse-9!";
    private const string Wrong = "definitely-not-it";

    private readonly ManualTimeProvider _clock = new();
    private readonly IAMDbContext _context;
    private readonly AuthService _auth;

    public LoginEnumerationTests()
    {
        _context = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        }).Build();

        _auth = AuthServiceTestFactory.Create(
            _context, configuration, loginThrottle: AuthServiceTestFactory.CreateLoginThrottle(timeProvider: _clock));
    }

    private async Task<User> AddUserAsync(string email = "owner@example.com", Action<User>? configure = null)
    {
        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 12),
            FirstName = "Owner",
            LastName = "Tester",
            EmailConfirmed = true,
            IsActive = true
        };
        configure?.Invoke(user);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    // A fresh address per call, so these tests never trip the throttle they are not about.
    private int _ipCounter;
    private string NextIp() => $"203.0.113.{++_ipCounter}";

    [Fact]
    public async Task UnknownEmail_WrongPassword_AndWrongPasswordOnALockedAccount_ReturnTheSameFailure()
    {
        var owner = await AddUserAsync();
        var locked = await AddUserAsync("locked@example.com", u =>
        {
            u.IsLockedOut = true;
            u.LockoutEnd = DateTime.UtcNow.AddMinutes(10);
            u.FailedLoginAttempts = 5;
        });

        var unknown = await _auth.LoginAsync("nobody@example.com", Wrong, NextIp());
        var wrongPassword = await _auth.LoginAsync(owner.Email, Wrong, NextIp());
        var wrongPasswordOnLocked = await _auth.LoginAsync(locked.Email, Wrong, NextIp());

        foreach (var result in new[] { unknown, wrongPassword, wrongPasswordOnLocked })
        {
            Assert.False(result.Success);
            Assert.Equal("Invalid email or password", result.Error);
            Assert.Equal(0, result.RetryAfterSeconds);
            Assert.False(result.RequiresTwoFactor);
            Assert.False(result.RequiresStepUp);
            Assert.Null(result.User);
            Assert.Null(result.AccessToken);
        }
    }

    [Fact]
    public async Task UnknownEmail_PaysTheSameBcryptCostAsAWrongPassword()
    {
        var owner = await AddUserAsync();
        // Warm up JIT / EF so only the BCrypt work separates the two measurements.
        await _auth.LoginAsync(owner.Email, Wrong, NextIp());

        var unknownTimes = new List<double>();
        var wrongPasswordTimes = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            unknownTimes.Add(await TimeAsync(() => _auth.LoginAsync($"nobody{i}@example.com", Wrong, NextIp())));
            wrongPasswordTimes.Add(await TimeAsync(() => _auth.LoginAsync(owner.Email, Wrong, NextIp())));
        }

        // Before the fix an unknown email returned in about a millisecond; a real BCrypt check at
        // work factor 12 takes a few hundred.
        Assert.True(unknownTimes.Min() >= wrongPasswordTimes.Min() * 0.5,
            $"unknown email answered in {unknownTimes.Min():F0} ms, wrong password in {wrongPasswordTimes.Min():F0} ms");
        Assert.True(unknownTimes.Min() >= 50, $"unknown email answered in {unknownTimes.Min():F0} ms, no BCrypt check ran");
    }

    private static async Task<double> TimeAsync(Func<Task<AuthResult>> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        return watch.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public async Task PasswordFailuresNoLongerLockTheUser()
    {
        var owner = await AddUserAsync();

        // 12 wrong passwords, each one after the throttle's wait, so every one reaches the password check.
        for (var i = 0; i < 12; i++)
        {
            var result = await _auth.LoginAsync(owner.Email, Wrong, "203.0.113.200");
            Assert.False(result.Success);
            Assert.Equal("Invalid email or password", result.Error);
            _clock.Advance(TimeSpan.FromSeconds(61));
        }

        await _context.Entry(owner).ReloadAsync();
        Assert.Equal(0, owner.FailedLoginAttempts);
        Assert.False(owner.IsLockedOut);
        Assert.Null(owner.LockoutEnd);
    }

    [Fact]
    public async Task RepeatedFailuresFromOneIp_AreAnsweredWithAWait_ForUnknownAndRealEmailsAlike()
    {
        var owner = await AddUserAsync();

        async Task<List<AuthResult>> Hammer(string email, string ip)
        {
            var results = new List<AuthResult>();
            for (var i = 0; i < 6; i++) results.Add(await _auth.LoginAsync(email, Wrong, ip));
            return results;
        }

        var real = await Hammer(owner.Email, "203.0.113.150");
        var unknown = await Hammer("nobody@example.com", "203.0.113.151");

        // Attempts 1-4 get the normal failure, 5 and 6 are told to wait - on the same attempt for both.
        Assert.Equal(real.Select(r => r.RetryAfterSeconds), unknown.Select(r => r.RetryAfterSeconds));
        Assert.Equal(real.Select(r => r.Error), unknown.Select(r => r.Error));
        Assert.All(real.Take(4), r => Assert.Equal(0, r.RetryAfterSeconds));
        Assert.All(real.Skip(4), r => Assert.Equal(2, r.RetryAfterSeconds));
        Assert.All(real.Skip(4), r => Assert.False(r.Success));
    }

    [Fact]
    public async Task TheCorrectPasswordFromAnotherIpIsNotBlockedBySomeoneElsesFailures()
    {
        var owner = await AddUserAsync();
        for (var i = 0; i < 8; i++) await _auth.LoginAsync(owner.Email, Wrong, "203.0.113.99");
        var blocked = await _auth.LoginAsync(owner.Email, Wrong, "203.0.113.99");
        Assert.True(blocked.RetryAfterSeconds > 0);

        var result = await _auth.LoginAsync(owner.Email, Password, "198.51.100.1");

        Assert.True(result.Success);
        Assert.NotNull(result.AccessToken);
    }

    [Fact]
    public async Task ABlockedCallerGetsNoAnswerAboutTheCorrectPassword()
    {
        var owner = await AddUserAsync();
        for (var i = 0; i < 4; i++) await _auth.LoginAsync(owner.Email, Wrong, "203.0.113.99");

        var withCorrectPassword = await _auth.LoginAsync(owner.Email, Password, "203.0.113.99");
        var withWrongPassword = await _auth.LoginAsync(owner.Email, Wrong, "203.0.113.99");

        Assert.False(withCorrectPassword.Success);
        Assert.Null(withCorrectPassword.AccessToken);
        Assert.Equal(withWrongPassword.Error, withCorrectPassword.Error);
        Assert.Equal(withWrongPassword.RetryAfterSeconds, withCorrectPassword.RetryAfterSeconds);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await _auth.LoginAsync(owner.Email, Password, "203.0.113.99")).Success);
    }

    [Fact]
    public async Task AccountLockedByTheTwoFactorStep_BlocksTheSignInOnlyAfterACorrectPassword_WithoutATimestamp()
    {
        var owner = await AddUserAsync(configure: u =>
        {
            u.TwoFactorEnabled = true;
            u.TwoFactorMethod = TwoFactorMethod.Email;
        });

        // Lock it the way task 4523 does: five wrong second-factor codes.
        for (var i = 0; i < 5; i++) await _auth.VerifyLoginTwoFactorAsync(owner.Id, "000000");
        await _context.Entry(owner).ReloadAsync();
        Assert.True(owner.IsLockedOut);
        Assert.True(owner.LockoutEnd > DateTime.UtcNow);

        var wrongPassword = await _auth.LoginAsync(owner.Email, Wrong, NextIp());
        var correctPassword = await _auth.LoginAsync(owner.Email, Password, NextIp());

        Assert.Equal("Invalid email or password", wrongPassword.Error);
        Assert.False(correctPassword.Success);
        Assert.Equal("Account is temporarily locked. Try again later.", correctPassword.Error);
        Assert.DoesNotContain("UTC", correctPassword.Error);
        Assert.DoesNotMatch(@"\d", correctPassword.Error!);
        Assert.Null(correctPassword.AccessToken);
        Assert.False(correctPassword.RequiresTwoFactor);
    }

    [Fact]
    public async Task AnExpiredTwoFactorLock_IsClearedByTheNextCorrectPassword()
    {
        var owner = await AddUserAsync(configure: u =>
        {
            u.TwoFactorEnabled = true;
            u.TwoFactorMethod = TwoFactorMethod.Email;
            u.FailedLoginAttempts = 5;
            u.IsLockedOut = true;
            u.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
        });

        var result = await _auth.LoginAsync(owner.Email, Password, NextIp());

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        await _context.Entry(owner).ReloadAsync();
        Assert.Equal(0, owner.FailedLoginAttempts);
        Assert.False(owner.IsLockedOut);
        Assert.Null(owner.LockoutEnd);
    }

    [Fact]
    public async Task ACorrectPassword_DoesNotResetTheTwoFactorCounterWhileASecondFactorIsPending()
    {
        var owner = await AddUserAsync(configure: u =>
        {
            u.TwoFactorEnabled = true;
            u.TwoFactorMethod = TwoFactorMethod.Email;
            u.FailedLoginAttempts = 3;
        });

        var result = await _auth.LoginAsync(owner.Email, Password, NextIp());

        Assert.True(result.RequiresTwoFactor);
        await _context.Entry(owner).ReloadAsync();
        Assert.Equal(3, owner.FailedLoginAttempts);
    }

    [Fact]
    public async Task ACorrectPassword_ResetsLeftoverCountersOfAUserWithoutTwoFactor()
    {
        var owner = await AddUserAsync(configure: u =>
        {
            u.FailedLoginAttempts = 4;
            u.IsLockedOut = true;
            u.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
        });

        var result = await _auth.LoginAsync(owner.Email, Password, NextIp());

        Assert.True(result.Success);
        Assert.NotNull(result.AccessToken);
        await _context.Entry(owner).ReloadAsync();
        Assert.Equal(0, owner.FailedLoginAttempts);
        Assert.False(owner.IsLockedOut);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-bcrypt-hash")]
    public async Task AStoredValueThatIsNotABcryptHash_FailsLikeAnyOtherWrongPassword(string storedHash)
    {
        var broken = await AddUserAsync("broken@example.com", u => u.PasswordHash = storedHash);

        var result = await _auth.LoginAsync(broken.Email, Password, NextIp());

        Assert.False(result.Success);
        Assert.Equal("Invalid email or password", result.Error);
    }

    [Fact]
    public async Task ANullPasswordOrEmailFailsLikeAnyOtherWrongPassword()
    {
        await AddUserAsync();

        var noPassword = await _auth.LoginAsync("owner@example.com", null!, NextIp());
        var noEmail = await _auth.LoginAsync(null!, Wrong, NextIp());

        Assert.Equal("Invalid email or password", noPassword.Error);
        Assert.Equal("Invalid email or password", noEmail.Error);
    }
}
