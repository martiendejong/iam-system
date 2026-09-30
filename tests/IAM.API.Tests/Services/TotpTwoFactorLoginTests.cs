using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4521: TOTP-enrolled accounts must be challenged for an authenticator code (or a
/// recovery code) before login completes, exactly like email 2FA already does - mirrors
/// EmailTwoFactorLoginTests.cs.
/// </summary>
public class TotpTwoFactorLoginTests
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

    private static User CreateUser(string password)
    {
        return new User
        {
            Email = "totp.user@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = true,
            IsActive = true,
            TwoFactorEnabled = false,
            TwoFactorMethod = TwoFactorMethod.None
        };
    }

    private static (AuthService authService, TotpService totpService, IAMDbContext context) CreateServices()
    {
        var context = CreateContext();
        var emailService = new NoOpEmailService();
        var smsService = new NoOpSmsService();
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" });
        var otpService = new OtpService(context, emailService, smsService, emailSettings, NullLogger<OtpService>.Instance);
        var riskAssessmentService = new RiskAssessmentService(context, NullLogger<RiskAssessmentService>.Instance);
        var claimsMappingService = new ClaimsMappingService(context);
        var totpService = new TotpService(context);
        var authService = new AuthService(context, CreateConfiguration(), emailService, riskAssessmentService, otpService, totpService, claimsMappingService, NullLogger<AuthService>.Instance);
        return (authService, totpService, context);
    }

    /// <summary>
    /// Enrolls and activates TOTP on the given user, returning the raw Base32 secret so the
    /// test can compute valid codes at will (mirrors the real enroll -> verify-and-activate flow).
    /// </summary>
    private static async Task<string> EnableTotpAsync(TotpService totpService, User user)
    {
        var setup = await totpService.EnableTotpAsync(user.Id);
        var code = ComputeTotpCode(setup.Secret);
        var activated = await totpService.VerifyAndActivateTotpAsync(user.Id, code);
        Assert.True(activated);
        return setup.Secret;
    }

    /// <summary>
    /// Re-implements TotpService's exact RFC 6238 (HMAC-SHA256) algorithm so tests can compute
    /// a currently-valid code from a known secret without reaching into TotpService internals.
    /// </summary>
    private static string ComputeTotpCode(string base32Secret)
    {
        var secretBytes = Base32Decode(base32Secret);
        var timeStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        var timeStepBytes = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            timeStepBytes[i] = (byte)(timeStep & 0xFF);
            timeStep >>= 8;
        }

        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(timeStepBytes);

        int offset = hash[^1] & 0x0F;
        int binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        int otp = binaryCode % 1_000_000;
        return otp.ToString().PadLeft(6, '0');
    }

    private static byte[] Base32Decode(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var normalized = base32.Trim().ToUpperInvariant().Replace("=", "").Replace(" ", "").Replace("-", "");

        var output = new List<byte>();
        int buffer = 0;
        int bitsLeft = 0;

        foreach (var c in normalized)
        {
            int value = alphabet.IndexOf(c);
            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)(buffer >> bitsLeft));
                buffer &= (1 << bitsLeft) - 1;
            }
        }

        return output.ToArray();
    }

    [Fact]
    public async Task LoginAsync_WithTotpEnabled_DoesNotIssueTokensAndSignalsTotpChallenge()
    {
        var (authService, totpService, context) = CreateServices();
        var user = CreateUser("Password123!");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await EnableTotpAsync(totpService, user);

        var result = await authService.LoginAsync(user.Email, "Password123!");

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Equal("totp", result.TwoFactorMethod);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task VerifyLoginTotpAsync_WithValidCode_CompletesLoginAndIssuesTokens()
    {
        var (authService, totpService, context) = CreateServices();
        var user = CreateUser("Password123!");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var secret = await EnableTotpAsync(totpService, user);

        var loginResult = await authService.LoginAsync(user.Email, "Password123!");
        Assert.True(loginResult.RequiresTwoFactor);

        var verifyResult = await authService.VerifyLoginTotpAsync(user.Id, ComputeTotpCode(secret));

        Assert.True(verifyResult.Success);
        Assert.NotNull(verifyResult.AccessToken);
        Assert.NotNull(verifyResult.RefreshToken);
    }

    [Fact]
    public async Task VerifyLoginTotpAsync_WithRecoveryCode_CompletesLoginAndIssuesTokens()
    {
        var (authService, totpService, context) = CreateServices();
        var user = CreateUser("Password123!");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await EnableTotpAsync(totpService, user);
        var recoveryCodes = await totpService.GenerateRecoveryCodesAsync(user.Id, count: 1);

        var loginResult = await authService.LoginAsync(user.Email, "Password123!");
        Assert.True(loginResult.RequiresTwoFactor);

        var verifyResult = await authService.VerifyLoginTotpAsync(user.Id, recoveryCodes[0]);

        Assert.True(verifyResult.Success);
        Assert.NotNull(verifyResult.AccessToken);
        Assert.NotNull(verifyResult.RefreshToken);
    }

    [Fact]
    public async Task VerifyLoginTotpAsync_WithWrongCode_Fails()
    {
        var (authService, totpService, context) = CreateServices();
        var user = CreateUser("Password123!");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await EnableTotpAsync(totpService, user);

        await authService.LoginAsync(user.Email, "Password123!");

        var verifyResult = await authService.VerifyLoginTotpAsync(user.Id, "000000");

        Assert.False(verifyResult.Success);
        Assert.Null(verifyResult.AccessToken);
        Assert.Null(verifyResult.RefreshToken);
    }

    [Fact]
    public async Task VerifyLoginTotpAsync_ForUserWithoutTotpEnabled_Fails()
    {
        var (authService, _, context) = CreateServices();
        var user = CreateUser("Password123!");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var verifyResult = await authService.VerifyLoginTotpAsync(user.Id, "123456");

        Assert.False(verifyResult.Success);
    }

    [Fact]
    public async Task CompletePasswordlessLoginAsync_WithTotpEnabled_DoesNotIssueTokensAndSignalsTotpChallenge()
    {
        // Regression coverage for the magic-link/passwordless 2FA bypass this task fixes
        // alongside the password-login path: an alternate primary factor must not skip the
        // account's TOTP requirement any more than it can skip email 2FA.
        var (authService, totpService, context) = CreateServices();
        var user = CreateUser("Password123!");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await EnableTotpAsync(totpService, user);

        var result = await authService.CompletePasswordlessLoginAsync(user);

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Equal("totp", result.TwoFactorMethod);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task CompletePasswordlessLoginAsync_WithTotpEnabled_CompletesViaVerifyLoginTotp()
    {
        var (authService, totpService, context) = CreateServices();
        var user = CreateUser("Password123!");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var secret = await EnableTotpAsync(totpService, user);

        var loginResult = await authService.CompletePasswordlessLoginAsync(user);
        Assert.True(loginResult.RequiresTwoFactor);

        var verifyResult = await authService.VerifyLoginTotpAsync(user.Id, ComputeTotpCode(secret));

        Assert.True(verifyResult.Success);
        Assert.NotNull(verifyResult.AccessToken);
        Assert.NotNull(verifyResult.RefreshToken);
    }

    [Fact]
    public async Task LoginAsync_WithoutTwoFactor_StillIssuesTokensDirectly()
    {
        // Regression: a user who never enrolled in any 2FA method must be unaffected.
        var (authService, _, context) = CreateServices();
        var user = CreateUser("Password123!");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await authService.LoginAsync(user.Email, "Password123!");

        Assert.True(result.Success);
        Assert.False(result.RequiresTwoFactor);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
    }

    private class NoOpEmailService : IEmailService
    {
        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }

    private class NoOpSmsService : ISmsService
    {
        public Task<bool> SendSmsAsync(string phoneNumber, string message) => Task.FromResult(true);
    }
}
