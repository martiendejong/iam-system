using System.Reflection;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5852: failures against ONE account from several distinct sources switch the account to an
/// e-mailed step-up code (never a lock) and raise one security alert, the signal survives a restart,
/// and an unknown e-mail looks exactly like a real one.
/// </summary>
public class AccountGuessingGuardTests
{
    private const string Password = "Correct-Horse-9!";
    private const string Wrong = "definitely-not-it";
    private const string Owner = "admin@example.com";

    private readonly ManualTimeProvider _clock = new();
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly RecordingEmailService _mail = new();
    private readonly List<(string Title, string Details)> _fired = new();
    private readonly IAMDbContext _context;
    private readonly AuthService _auth;
    private int _ip;

    public AccountGuessingGuardTests()
    {
        _context = NewContext();
        _auth = BuildAuth(_context, _mail, MakeAlerts(_fired));
    }

    private IAMDbContext NewContext() => new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static IConfiguration Config(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        };
        if (extra != null) foreach (var kv in extra) values[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private AccountGuessingGuard BuildGuard(IAMDbContext context, ISecurityAlertService? alerts, IConfiguration? config = null) =>
        new(context, config ?? Config(), NullLogger<AccountGuessingGuard>.Instance, alerts, _clock);

    private AuthService BuildAuth(IAMDbContext context, RecordingEmailService mail, ISecurityAlertService? alerts, IConfiguration? config = null) =>
        AuthServiceTestFactory.Create(context, config ?? Config(), mail,
            AuthServiceTestFactory.CreateLoginThrottle(timeProvider: _clock),
            BuildGuard(context, alerts, config));

    private async Task<User> AddUserAsync(string email = Owner)
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
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private string NextIp() => $"198.51.100.{++_ip}";

    private async Task FailFromAsync(AuthService auth, string email, int sources)
    {
        for (var i = 0; i < sources; i++)
        {
            var result = await auth.LoginAsync(email, Wrong, NextIp());
            Assert.False(result.Success);
            Assert.Equal(AuthService.InvalidCredentialsMessage, result.Error);
        }
    }

    [Fact]
    public async Task BelowThreshold_CorrectPasswordSignsInNormally()
    {
        await AddUserAsync();
        await FailFromAsync(_auth, Owner, 3);

        var result = await _auth.LoginAsync(Owner, Password, NextIp());

        Assert.True(result.Success);
        Assert.False(result.RequiresStepUp);
        Assert.NotNull(result.AccessToken);
        Assert.Empty(_fired);
    }

    [Fact]
    public async Task ManyDistinctSources_CorrectPasswordNeedsEmailedCode_AndAlertFiresOnce()
    {
        var owner = await AddUserAsync();
        await FailFromAsync(_auth, Owner, 4);
        await FailFromAsync(_auth, Owner, 3); // more sources must not alert again within the window

        var result = await _auth.LoginAsync(Owner, Password, NextIp());

        Assert.True(result.Success);
        Assert.True(result.RequiresStepUp);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Single(_mail.SentMfaCodes);
        Assert.Equal(Owner, _mail.SentMfaCodes[0].Email);

        var alert = Assert.Single(_fired);
        Assert.DoesNotContain(Password, alert.Details);
        Assert.DoesNotContain(Wrong, alert.Details);
        Assert.DoesNotContain("198.51.100", alert.Details);
        Assert.DoesNotContain(Owner, alert.Details);
        Assert.Contains(owner.Id.ToString(), alert.Details);

        // The failures never locked the account.
        var stored = await _context.Users.AsNoTracking().SingleAsync(u => u.Id == owner.Id);
        Assert.False(stored.IsLockedOut);
        Assert.Equal(0, stored.FailedLoginAttempts);
    }

    [Fact]
    public async Task RealOwner_CanFinishSignInWithPasswordPlusEmailedCode()
    {
        await AddUserAsync();
        await FailFromAsync(_auth, Owner, 4);

        var step = await _auth.LoginAsync(Owner, Password, NextIp());
        Assert.True(step.RequiresStepUp);

        var wrong = await _auth.VerifyStepUpAsync(Owner, "000000", NextIp());
        Assert.False(wrong.Success);

        var done = await _auth.VerifyStepUpAsync(Owner, _mail.SentMfaCodes.Single().Code, NextIp());
        Assert.True(done.Success);
        Assert.NotNull(done.AccessToken);
    }

    [Fact]
    public async Task WindowExpiry_LiftsTheStepUp()
    {
        await AddUserAsync();
        await FailFromAsync(_auth, Owner, 4);
        Assert.True((await _auth.LoginAsync(Owner, Password, NextIp())).RequiresStepUp);

        _clock.Advance(TimeSpan.FromMinutes(31));

        var result = await _auth.LoginAsync(Owner, Password, NextIp());
        Assert.False(result.RequiresStepUp);
        Assert.NotNull(result.AccessToken);
    }

    [Fact]
    public async Task SignalSurvivesRestart()
    {
        await AddUserAsync();
        await FailFromAsync(_auth, Owner, 4);

        // "Restart": brand new context, throttle, guard and AuthService over the same database.
        var restarted = BuildAuth(NewContext(), new RecordingEmailService(), MakeAlerts(new()));

        var result = await restarted.LoginAsync(Owner, Password, NextIp());
        Assert.True(result.RequiresStepUp);
        Assert.Null(result.AccessToken);
    }

    [Fact]
    public async Task SameSourceRepeating_DoesNotCountAsDistinctSources()
    {
        await AddUserAsync();
        // The per-pair throttle slows one caller, so step the clock between attempts.
        for (var i = 0; i < 6; i++)
        {
            await _auth.LoginAsync(Owner, Wrong, "198.51.100.200");
            _clock.Advance(TimeSpan.FromSeconds(70));
        }

        var result = await _auth.LoginAsync(Owner, Password, "198.51.100.201");
        Assert.False(result.RequiresStepUp);
        Assert.NotNull(result.AccessToken);
    }

    [Fact]
    public async Task UnknownEmail_LooksTheSame_StoresNoReadableValues_AndRaisesNoAlert()
    {
        await AddUserAsync();
        const string ghost = "ghost@example.com";

        await FailFromAsync(_auth, ghost, 5);
        await FailFromAsync(_auth, Owner, 5);

        // Same persisted shape for the invented address as for the real one.
        Assert.Equal(5, await _context.LoginFailureSignals.CountAsync(s => s.AccountKey == AccountKeyOf(ghost)));
        Assert.Equal(5, await _context.LoginFailureSignals.CountAsync(s => s.AccountKey == AccountKeyOf(Owner)));

        foreach (var row in await _context.LoginFailureSignals.ToListAsync())
        {
            Assert.DoesNotContain("example.com", row.AccountKey);
            Assert.DoesNotContain("198.51.100", row.SourceKey);
            Assert.Equal(64, row.AccountKey.Length);
        }

        Assert.Single(_fired); // only the real account alerted
        Assert.Empty(_mail.SentMfaCodes); // nobody was e-mailed: no password was proven
    }

    private string AccountKeyOf(string email)
    {
        var key = typeof(AccountGuessingGuard).GetMethod("Key", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string)key.Invoke(BuildGuard(_context, null), new object[] { "a:" + email })!;
    }

    [Fact]
    public async Task StepUpEmail_IsRateCappedPerAddress()
    {
        await AddUserAsync();
        await FailFromAsync(_auth, Owner, 4);

        for (var i = 0; i < 15; i++)
        {
            var result = await _auth.LoginAsync(Owner, Password, NextIp());
            Assert.True(result.RequiresStepUp);
            Assert.Null(result.AccessToken);
        }

        Assert.True(_mail.SentMfaCodes.Count <= 10, $"sent {_mail.SentMfaCodes.Count} codes");
    }

    [Fact]
    public async Task ThresholdIsConfigurable_AndZeroDisables()
    {
        await AddUserAsync();
        var two = BuildAuth(_context, new RecordingEmailService(), MakeAlerts(new()),
            Config(new() { ["RateLimiting:AccountGuessDistinctSources"] = "2" }));
        await FailFromAsync(two, Owner, 2);
        Assert.True((await two.LoginAsync(Owner, Password, NextIp())).RequiresStepUp);

        await AddUserAsync("other@example.com");
        var off = BuildAuth(_context, new RecordingEmailService(), MakeAlerts(new()),
            Config(new() { ["RateLimiting:AccountGuessDistinctSources"] = "0" }));
        await FailFromAsync(off, "other@example.com", 8);
        Assert.False((await off.LoginAsync("other@example.com", Password, NextIp())).RequiresStepUp);
    }

    private static ISecurityAlertService MakeAlerts(List<(string Title, string Details)> fired)
    {
        var service = DispatchProxy.Create<ISecurityAlertService, AlertProxy>();
        ((AlertProxy)(object)service).Fired = fired;
        return service;
    }

    /// <summary>Records FireAlertAsync calls; every other member is unused here.</summary>
    public class AlertProxy : DispatchProxy
    {
        public List<(string Title, string Details)> Fired { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ISecurityAlertService.FireAlertAsync))
            {
                Fired.Add(((string)args![1]!, (string?)args[2] ?? string.Empty));
                return Task.FromResult(new SecurityAlert { Title = (string)args[1]! });
            }
            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
