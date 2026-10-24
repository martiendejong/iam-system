using IAM.Core.Services;
using IAM.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5166: the per (typed email, IP) progressive delay and the per-IP failed-login cap that
/// replace the password hard lock. The clock is moved by hand, so none of these tests sleep.
/// </summary>
public class LoginThrottleTests
{
    private const string Ip = "198.51.100.7";
    private const string Email = "victim@example.com";

    private readonly ManualTimeProvider _clock = new();

    private LoginThrottle Create(Dictionary<string, string?>? settings = null) =>
        AuthServiceTestFactory.CreateLoginThrottle(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build(),
            _clock);

    [Fact]
    public void FirstThreeFailuresAreFree_ThenTheDelayDoublesUpToSixtySeconds()
    {
        var throttle = Create();

        // Failures 1-3 can follow each other instantly.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);
        }

        // Each further failure is still looked at, and then makes the caller wait 2, 4, 8 ... 60 s.
        foreach (var expectedWait in new[] { 2, 4, 8, 16, 32, 60, 60 })
        {
            Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);

            var refused = throttle.BeginAttempt(Email, Ip);
            Assert.False(refused.Allowed);
            Assert.Equal(expectedWait, refused.RetryAfterSeconds);

            _clock.Advance(TimeSpan.FromSeconds(expectedWait));
        }
    }

    [Fact]
    public void RetryAfterCountsDownAndRoundsUp()
    {
        var throttle = Create();
        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, Ip);

        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, throttle.BeginAttempt(Email, Ip).RetryAfterSeconds); // 1.5 s left

        _clock.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.Equal(1, throttle.BeginAttempt(Email, Ip).RetryAfterSeconds); // 0.5 s left

        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);
    }

    [Fact]
    public void RefusedAttemptsAreNotCounted_SoHammeringDoesNotExtendTheWait()
    {
        var throttle = Create();
        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, Ip);

        for (var i = 0; i < 100; i++)
        {
            Assert.False(throttle.BeginAttempt(Email, Ip).Allowed);
        }

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);
        Assert.Equal(4, throttle.BeginAttempt(Email, Ip).RetryAfterSeconds); // not 8 or more
    }

    [Fact]
    public void AnotherIpIsNotBlockedByThisIpsFailures()
    {
        var throttle = Create();
        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, Ip);
        Assert.False(throttle.BeginAttempt(Email, Ip).Allowed);

        Assert.True(throttle.BeginAttempt(Email, "198.51.100.8").Allowed);
    }

    [Fact]
    public void AnotherEmailFromTheSameIpHasItsOwnFreeFailures()
    {
        var throttle = Create();
        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, Ip);
        Assert.False(throttle.BeginAttempt(Email, Ip).Allowed);

        Assert.True(throttle.BeginAttempt("someone.else@example.com", Ip).Allowed);
    }

    [Theory]
    [InlineData("VICTIM@example.com")]
    [InlineData("  victim@example.com  ")]
    [InlineData("Victim@Example.COM\t")]
    public void EmailCaseAndWhitespaceCannotEscapeTheThrottle(string variant)
    {
        var throttle = Create();
        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, Ip);

        var refused = throttle.BeginAttempt(variant, Ip);

        Assert.False(refused.Allowed);
        Assert.Equal(2, refused.RetryAfterSeconds);
    }

    [Fact]
    public void RotatingEmailCasesShareOneAllowance()
    {
        var throttle = Create();
        var variants = new[] { "victim@example.com", "Victim@example.com", " VICTIM@EXAMPLE.COM", "victim@example.com ", "vIcTiM@example.com" };

        var allowed = variants.Count(v => throttle.BeginAttempt(v, Ip).Allowed);

        Assert.Equal(4, allowed); // 3 free + the one that starts the wait
    }

    [Fact]
    public void AnUnknownEmailIsThrottledLikeAnyOther()
    {
        // The throttle never looks at accounts: a made-up address behaves exactly like a real one.
        var throttle = Create();
        for (var i = 0; i < 4; i++) Assert.True(throttle.BeginAttempt("nobody.has.this@example.com", Ip).Allowed);

        var refused = throttle.BeginAttempt("nobody.has.this@example.com", Ip);

        Assert.False(refused.Allowed);
        Assert.Equal(2, refused.RetryAfterSeconds);
    }

    [Fact]
    public void IPv6AddressesInTheSame64ShareOneBucket_AndMappedIPv4SharesWithIPv4()
    {
        var throttle = Create();
        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, "2001:db8:1:2::1");

        Assert.False(throttle.BeginAttempt(Email, "2001:db8:1:2:aaaa:bbbb:cccc:dddd").Allowed);
        Assert.True(throttle.BeginAttempt(Email, "2001:db8:1:3::1").Allowed);

        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, "203.0.113.9");
        Assert.False(throttle.BeginAttempt(Email, "::ffff:203.0.113.9").Allowed);
    }

    [Fact]
    public void RecordSuccessClearsThePairSoTheOwnerIsNotKeptWaiting()
    {
        var throttle = Create();
        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, Ip);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(throttle.BeginAttempt(Email, Ip).Allowed); // 5th attempt is the right password
        throttle.RecordSuccess(Email, Ip);

        // The next sign-in starts from zero again: three free failures, no leftover delay.
        for (var i = 0; i < 3; i++) Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);
    }

    [Fact]
    public void PairForgetsItsFailuresAfterBeingIdle()
    {
        var throttle = Create();
        for (var i = 0; i < 4; i++) throttle.BeginAttempt(Email, Ip);
        Assert.False(throttle.BeginAttempt(Email, Ip).Allowed);

        _clock.Advance(TimeSpan.FromMinutes(16));

        for (var i = 0; i < 4; i++) Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);
        Assert.Equal(2, throttle.BeginAttempt(Email, Ip).RetryAfterSeconds);
    }

    [Fact]
    public void PerIpCapRefusesAnIpThatSpreadsFailuresOverManyEmails()
    {
        var throttle = Create();

        // 30 failures, every one against a different (possibly invented) email: each has its own free
        // failures, so only the per-IP cap can stop this.
        for (var i = 0; i < 30; i++)
        {
            Assert.True(throttle.BeginAttempt($"user{i}@example.com", Ip).Allowed);
        }

        var refused = throttle.BeginAttempt("user30@example.com", Ip);
        Assert.False(refused.Allowed);
        Assert.Equal(900, refused.RetryAfterSeconds);

        // Other addresses are unaffected, and the cap drains with the window.
        Assert.True(throttle.BeginAttempt("user0@example.com", "198.51.100.99").Allowed);
        _clock.Advance(TimeSpan.FromSeconds(900));
        Assert.True(throttle.BeginAttempt("user31@example.com", Ip).Allowed);
    }

    [Fact]
    public void RecordSuccessGivesTheIpSlotBack()
    {
        var throttle = Create(new Dictionary<string, string?> { ["RateLimiting:LoginIpFailureLimit"] = "2" });

        Assert.True(throttle.BeginAttempt("a@example.com", Ip).Allowed);
        throttle.RecordSuccess("a@example.com", Ip); // a correct password is not a failure

        Assert.True(throttle.BeginAttempt("b@example.com", Ip).Allowed);
        Assert.True(throttle.BeginAttempt("c@example.com", Ip).Allowed);
        Assert.False(throttle.BeginAttempt("d@example.com", Ip).Allowed);
    }

    [Fact]
    public void ParallelRequestsCannotSlipThroughBeforeTheFirstFailureIsRecorded()
    {
        var throttle = Create();
        var allowed = 0;

        Parallel.For(0, 200, _ =>
        {
            if (throttle.BeginAttempt(Email, Ip).Allowed) Interlocked.Increment(ref allowed);
        });

        Assert.Equal(4, allowed); // 3 free + the one that starts the wait
    }

    [Fact]
    public void ConfigurationChangesTheCurve_AndZeroDisablesTheIpCap()
    {
        var throttle = Create(new Dictionary<string, string?>
        {
            ["RateLimiting:LoginFreeFailures"] = "1",
            ["RateLimiting:LoginMaxDelaySeconds"] = "5",
            ["RateLimiting:LoginIpFailureLimit"] = "0"
        });

        Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);   // free
        Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);   // starts a 2 s wait
        Assert.Equal(2, throttle.BeginAttempt(Email, Ip).RetryAfterSeconds);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(throttle.BeginAttempt(Email, Ip).Allowed);
        Assert.Equal(5, throttle.BeginAttempt(Email, Ip).RetryAfterSeconds); // capped at 5

        for (var i = 0; i < 200; i++)
        {
            Assert.True(throttle.BeginAttempt($"u{i}@example.com", "198.51.100.50").Allowed);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("not an ip", "not an email")]
    public void MissingOrOddInputIsHandledWithoutThrowing(string? email, string? ip)
    {
        var throttle = Create();

        Assert.True(throttle.BeginAttempt(email, ip).Allowed);
        throttle.RecordSuccess(email, ip);
    }

    [Fact]
    public void ARidiculouslyLongEmailIsStillJustOneKey()
    {
        var throttle = Create();
        var huge = new string('a', 1_000_000) + "@example.com";

        for (var i = 0; i < 4; i++) throttle.BeginAttempt(huge, Ip);

        Assert.False(throttle.BeginAttempt(huge.ToUpperInvariant(), Ip).Allowed);
    }
}
