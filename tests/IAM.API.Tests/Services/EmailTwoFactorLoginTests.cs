using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Services;

public class EmailTwoFactorLoginTests
{
    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IAMDbContext(options);
    }

    private static IConfiguration CreateConfiguration()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static User CreateUser(string password, bool emailTwoFactorEnabled)
    {
        return new User
        {
            Email = "user@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = true,
            IsActive = true,
            TwoFactorEnabled = emailTwoFactorEnabled,
            TwoFactorMethod = emailTwoFactorEnabled ? TwoFactorMethod.Email : TwoFactorMethod.None
        };
    }

    private static (AuthService authService, OtpService otpService, FakeEmailService emailService, IAMDbContext context) CreateServices()
    {
        var context = CreateContext();
        var emailService = new FakeEmailService();
        var smsService = new FakeSmsService();
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" });
        var otpService = new OtpService(context, emailService, smsService, emailSettings, NullLogger<OtpService>.Instance);
        var riskAssessmentService = new RiskAssessmentService(context, NullLogger<RiskAssessmentService>.Instance);
        var claimsMappingService = new ClaimsMappingService(context);
        var authService = new AuthService(context, CreateConfiguration(), emailService, riskAssessmentService, otpService, claimsMappingService);
        return (authService, otpService, emailService, context);
    }

    [Fact]
    public async Task LoginAsync_WithEmailTwoFactorEnabled_DoesNotIssueTokensAndSendsCode()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await authService.LoginAsync(user.Email, "Password123!");

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Single(emailService.SentTwoFactorCodes);
        Assert.Equal(user.Email, emailService.SentTwoFactorCodes[0].Email);
    }

    [Fact]
    public async Task LoginAsync_WithoutTwoFactor_IssuesTokensDirectly()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: false);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await authService.LoginAsync(user.Email, "Password123!");

        Assert.True(result.Success);
        Assert.False(result.RequiresTwoFactor);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.Empty(emailService.SentTwoFactorCodes);
    }

    [Fact]
    public async Task VerifyLoginTwoFactorAsync_WithCodeFromEmail_CompletesLogin()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var loginResult = await authService.LoginAsync(user.Email, "Password123!");
        Assert.True(loginResult.RequiresTwoFactor);
        var sentCode = emailService.SentTwoFactorCodes[0].Code;

        var verifyResult = await authService.VerifyLoginTwoFactorAsync(user.Id, sentCode);

        Assert.True(verifyResult.Success);
        Assert.NotNull(verifyResult.AccessToken);
        Assert.NotNull(verifyResult.RefreshToken);
    }

    [Fact]
    public async Task VerifyLoginTwoFactorAsync_WithWrongCode_Fails()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        await authService.LoginAsync(user.Email, "Password123!");
        Assert.NotEmpty(emailService.SentTwoFactorCodes);

        var verifyResult = await authService.VerifyLoginTwoFactorAsync(user.Id, "000000");

        Assert.False(verifyResult.Success);
        Assert.Null(verifyResult.AccessToken);
    }

    [Fact]
    public async Task LoginAsync_WithReturnUrl_IncludesItInTheEmailedVerifyLink()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        await authService.LoginAsync(user.Email, "Password123!", returnUrl: "/connect/authorize?client_id=jengo-agi");

        Assert.Single(emailService.SentVerifyUrls);
        Assert.Contains("returnUrl=%2Fconnect%2Fauthorize%3Fclient_id%3Djengo-agi", emailService.SentVerifyUrls[0]);
    }

    [Fact]
    public async Task LoginAsync_WithoutReturnUrl_OmitsItFromTheEmailedVerifyLink()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        await authService.LoginAsync(user.Email, "Password123!");

        Assert.Single(emailService.SentVerifyUrls);
        Assert.DoesNotContain("returnUrl=", emailService.SentVerifyUrls[0]);
    }

    [Fact]
    public async Task ResendLoginTwoFactorCodeAsync_WithReturnUrl_IncludesItInTheEmailedVerifyLink()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        await authService.LoginAsync(user.Email, "Password123!");
        await authService.ResendLoginTwoFactorCodeAsync(user.Id, "/portal/profile");

        Assert.Equal(2, emailService.SentVerifyUrls.Count);
        Assert.Contains("returnUrl=%2Fportal%2Fprofile", emailService.SentVerifyUrls[1]);
    }

    [Fact]
    public async Task CompletePasswordlessLoginAsync_WithEmailTwoFactorEnabled_DoesNotIssueTokensAndSendsCode()
    {
        // Regression test for the magic-link 2FA bypass (task 869eft100): an alternate
        // primary factor (magic link, passwordless OTP) must not skip the account's
        // email 2FA requirement any more than a password login can.
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await authService.CompletePasswordlessLoginAsync(user);

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Single(emailService.SentTwoFactorCodes);
        Assert.Equal(user.Email, emailService.SentTwoFactorCodes[0].Email);
    }

    [Fact]
    public async Task CompletePasswordlessLoginAsync_WithEmailTwoFactorEnabled_CompletesViaVerifyLoginTwoFactor()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var loginResult = await authService.CompletePasswordlessLoginAsync(user);
        Assert.True(loginResult.RequiresTwoFactor);
        var sentCode = emailService.SentTwoFactorCodes[0].Code;

        var verifyResult = await authService.VerifyLoginTwoFactorAsync(user.Id, sentCode);

        Assert.True(verifyResult.Success);
        Assert.NotNull(verifyResult.AccessToken);
        Assert.NotNull(verifyResult.RefreshToken);
    }

    [Fact]
    public async Task CompletePasswordlessLoginAsync_WithoutTwoFactor_IssuesTokensDirectly()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: false);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await authService.CompletePasswordlessLoginAsync(user);

        Assert.True(result.Success);
        Assert.False(result.RequiresTwoFactor);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.Empty(emailService.SentTwoFactorCodes);
    }

    [Fact]
    public async Task VerifyLoginTwoFactorAsync_ForUserWithoutEmailTwoFactor_Fails()
    {
        var (authService, _, _, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: false);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var verifyResult = await authService.VerifyLoginTwoFactorAsync(user.Id, "123456");

        Assert.False(verifyResult.Success);
    }

    [Fact]
    public async Task VerifyLoginTwoFactorAsync_CodeIsSingleUse()
    {
        var (authService, _, emailService, context) = CreateServices();
        var user = CreateUser("Password123!", emailTwoFactorEnabled: true);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        await authService.LoginAsync(user.Email, "Password123!");
        var sentCode = emailService.SentTwoFactorCodes[0].Code;

        var firstAttempt = await authService.VerifyLoginTwoFactorAsync(user.Id, sentCode);
        var secondAttempt = await authService.VerifyLoginTwoFactorAsync(user.Id, sentCode);

        Assert.True(firstAttempt.Success);
        Assert.False(secondAttempt.Success);
    }

    private class FakeEmailService : IEmailService
    {
        public List<(string Email, string Code)> SentTwoFactorCodes { get; } = new();
        public List<string> SentVerifyUrls { get; } = new();

        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default)
        {
            SentTwoFactorCodes.Add((email, code));
            SentVerifyUrls.Add(verifyUrl);
            return Task.CompletedTask;
        }

        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }

    private class FakeSmsService : ISmsService
    {
        public Task<bool> SendSmsAsync(string phoneNumber, string message) => Task.FromResult(true);
    }
}
