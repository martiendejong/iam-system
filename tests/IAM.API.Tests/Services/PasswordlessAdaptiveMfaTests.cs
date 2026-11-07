using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Services;

/// <summary>Task 5630: passwordless logins go through the same adaptive-MFA gate as password login.</summary>
public class PasswordlessAdaptiveMfaTests
{
    private static (AuthService auth, StubRisk risk, FakeEmail email, IAMDbContext ctx) Create()
    {
        var ctx = new IAMDbContext(new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var email = new FakeEmail();
        var otp = new OtpService(ctx, email, new FakeSms(), Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" }), NullLogger<OtpService>.Instance);
        var risk = new StubRisk();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        }).Build();
        var auth = new AuthService(ctx, config, email, risk, otp, new ClaimsMappingService(ctx), NullLogger<AuthService>.Instance,
            AuthServiceTestFactory.CreateLoginThrottle(), AuthServiceTestFactory.CreateChallengeService());
        return (auth, risk, email, ctx);
    }

    private static async Task<User> AddUserAsync(IAMDbContext ctx, bool active = true, bool emailTwoFactor = false)
    {
        var user = new User
        {
            Email = "user@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = true,
            IsActive = active,
            TwoFactorEnabled = emailTwoFactor,
            TwoFactorMethod = emailTwoFactor ? TwoFactorMethod.Email : TwoFactorMethod.None
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Allow_IssuesTokens_AndScoresWithCallerIpAndAgent()
    {
        var (auth, risk, _, ctx) = Create();
        var user = await AddUserAsync(ctx);

        var result = await auth.CompletePasswordlessLoginAsync(user, "203.0.113.5", "TestBrowser/1");

        Assert.True(result.Success);
        Assert.NotNull(result.AccessToken);
        Assert.Equal(1, risk.Calls);
        Assert.Equal("203.0.113.5", risk.LastIp);
        Assert.Equal("TestBrowser/1", risk.LastAgent);
    }

    [Fact]
    public async Task Block_RefusesLogin_WithoutTokens()
    {
        var (auth, risk, _, ctx) = Create();
        risk.Action = RiskAction.Block;
        var user = await AddUserAsync(ctx);

        var result = await auth.CompletePasswordlessLoginAsync(user, "203.0.113.5", "TestBrowser/1");

        Assert.False(result.Success);
        Assert.Null(result.AccessToken);
        Assert.Contains("unusual account activity", result.Error);
        Assert.Empty(ctx.RefreshTokens);
    }

    [Fact]
    public async Task StepUp_EmailsCode_NoTokens_AndCompletesByUserId_WithoutRescoring()
    {
        var (auth, risk, email, ctx) = Create();
        risk.Action = RiskAction.StepUp;
        var user = await AddUserAsync(ctx);

        var result = await auth.CompletePasswordlessLoginAsync(user, "203.0.113.5", "TestBrowser/1");

        Assert.True(result.Success);
        Assert.True(result.RequiresStepUp);
        Assert.Null(result.AccessToken);
        Assert.Single(email.MfaCodes);
        Assert.Equal(1, risk.Calls);

        var wrong = await auth.VerifyStepUpByUserIdAsync(user.Id, "000000");
        Assert.False(wrong.Success);

        var verified = await auth.VerifyStepUpByUserIdAsync(user.Id, email.MfaCodes[0]);
        Assert.True(verified.Success);
        Assert.NotNull(verified.AccessToken);
        Assert.Equal(1, risk.Calls); // a verified code is not scored again
    }

    [Fact]
    public async Task EmailTwoFactor_StillChallenged_AndNotScored()
    {
        var (auth, risk, _, ctx) = Create();
        risk.Action = RiskAction.Block;
        var user = await AddUserAsync(ctx, emailTwoFactor: true);

        var result = await auth.CompletePasswordlessLoginAsync(user, "203.0.113.5", "TestBrowser/1");

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Equal(0, risk.Calls);
    }

    [Fact]
    public async Task InactiveAccount_IsRefused_BeforeScoring()
    {
        var (auth, risk, _, ctx) = Create();
        var user = await AddUserAsync(ctx, active: false);

        var result = await auth.CompletePasswordlessLoginAsync(user, "203.0.113.5", "TestBrowser/1");

        Assert.False(result.Success);
        Assert.Equal("Account is inactive", result.Error);
        Assert.Equal(0, risk.Calls);
    }

    private class StubRisk : IRiskAssessmentService
    {
        public RiskAction Action { get; set; } = RiskAction.Allow;
        public int Calls { get; private set; }
        public string? LastIp { get; private set; }
        public string? LastAgent { get; private set; }

        public Task<LoginRiskScore> AssessLoginRiskAsync(Guid userId, string ipAddress, string? userAgent, string? deviceFingerprint = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastIp = ipAddress;
            LastAgent = userAgent;
            return Task.FromResult(new LoginRiskScore { UserId = userId, IpAddress = ipAddress, Action = Action });
        }

        public Task<List<LoginRiskScore>> GetRiskScoresAsync(Guid? userId = null, DateTime? startDate = null, DateTime? endDate = null, int skip = 0, int take = 50, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RiskDashboardData> GetDashboardDataAsync(Guid? tenantId = null, int days = 30, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<RiskThreshold>> GetThresholdsAsync(Guid? tenantId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RiskThreshold?> GetThresholdByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RiskThreshold> UpsertThresholdAsync(RiskThreshold threshold, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> DeleteThresholdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<TrustedDevice>> GetTrustedDevicesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TrustedDevice> TrustDeviceAsync(Guid userId, string deviceFingerprint, string name, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RemoveTrustedDeviceAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private class FakeEmail : IEmailService
    {
        public List<string> MfaCodes { get; } = new();
        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) { MfaCodes.Add(code); return Task.CompletedTask; }
        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task<bool> SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.FromResult(true);
        public Task<bool> SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }

    private class FakeSms : ISmsService
    {
        public Task<bool> SendSmsAsync(string phoneNumber, string message) => Task.FromResult(true);
    }
}
