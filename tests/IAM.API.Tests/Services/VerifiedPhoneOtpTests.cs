using System.Text.RegularExpressions;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Services;

/// <summary>
/// SMS-OTP login only resolves accounts through a phone number the user proved to hold (task 5492).
/// </summary>
public class VerifiedPhoneOtpTests
{
    private const string Phone = "+31600000002";

    private static (IAMDbContext ctx, CapturingSms sms, OtpService otp) Setup()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var ctx = new IAMDbContext(options);
        var sms = new CapturingSms();
        var otp = new OtpService(ctx, new NullEmail(), sms,
            Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com/auth" }),
            NullLogger<OtpService>.Instance, new InMemoryRequestThrottle());
        return (ctx, sms, otp);
    }

    private static async Task<User> AddUserAsync(IAMDbContext ctx, string email, string? phone, bool verified)
    {
        var user = new User
        {
            Email = email, PasswordHash = "x", FirstName = "T", LastName = "U",
            EmailConfirmed = true, IsActive = true, PhoneNumber = phone
        };
        if (verified) user.MarkPhoneVerified(PhoneNumberNormalizer.Normalize(phone)!);
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task SmsLogin_UnverifiedNumber_SendsNothing_AndReturnsTheSameResult()
    {
        var (ctx, sms, otp) = Setup();
        await AddUserAsync(ctx, "a@example.com", Phone, verified: false);

        Assert.True(await otp.SendSmsOtpAsync(Phone, OtpPurpose.Login));

        Assert.Empty(sms.Messages);
        Assert.Empty(ctx.OtpCodes);
    }

    [Fact]
    public async Task SmsLogin_VerifiedNumber_SendsCode_ForAnyWrittenFormOfTheNumber()
    {
        var (ctx, sms, otp) = Setup();
        var user = await AddUserAsync(ctx, "a@example.com", Phone, verified: true);

        Assert.True(await otp.SendSmsOtpAsync("+31 6 0000-0002", OtpPurpose.Login));

        Assert.Single(sms.Messages);
        var code = sms.LastCode();
        // Verify side uses the same normalization, so another spelling of the number validates too.
        Assert.True(await otp.ValidateOtpAsync(null, "0031600000002", code, OtpPurpose.Login));
        Assert.Equal(user.Id, ctx.OtpCodes.Single().UserId);
    }

    [Fact]
    public async Task SmsLogin_NumberMatchingTwoAccounts_IsRefused()
    {
        var (ctx, sms, otp) = Setup();
        // InMemory does not enforce the unique index; this simulates legacy/corrupt data.
        await AddUserAsync(ctx, "a@example.com", Phone, verified: true);
        await AddUserAsync(ctx, "b@example.com", Phone, verified: true);

        Assert.True(await otp.SendSmsOtpAsync(Phone, OtpPurpose.Login));

        Assert.Empty(sms.Messages);
    }

    [Fact]
    public async Task ChangingThePhoneNumber_ClearsTheVerifiedState_ButKeepingItDoesNot()
    {
        var (ctx, _, _) = Setup();
        var user = await AddUserAsync(ctx, "a@example.com", Phone, verified: true);

        user.SetPhoneNumber("+31 6 00000002"); // same number, different spelling
        Assert.Equal(Phone, user.VerifiedPhoneNumber);

        user.SetPhoneNumber("+31611111111");
        Assert.Null(user.VerifiedPhoneNumber);
        Assert.Null(user.PhoneVerifiedAt);
        Assert.Equal("+31611111111", user.PhoneNumber);

        user.SetPhoneNumber(null);
        Assert.Null(user.PhoneNumber);
    }

    [Fact]
    public async Task PhoneVerification_RightCode_MarksTheNumberVerified_AndEnablesSmsLogin()
    {
        var (ctx, sms, otp) = Setup();
        var user = await AddUserAsync(ctx, "a@example.com", Phone, verified: false);

        Assert.Equal(PhoneVerificationSendResult.Sent, await otp.SendPhoneVerificationAsync(user));
        Assert.Equal(PhoneVerificationConfirmResult.InvalidCode, await otp.ConfirmPhoneVerificationAsync(user, "000000" == sms.LastCode() ? "111111" : "000000"));
        Assert.Null(user.VerifiedPhoneNumber);

        Assert.Equal(PhoneVerificationConfirmResult.Verified, await otp.ConfirmPhoneVerificationAsync(user, sms.LastCode()));
        Assert.Equal(Phone, user.VerifiedPhoneNumber);
        Assert.NotNull(user.PhoneVerifiedAt);

        sms.Messages.Clear();
        await otp.SendSmsOtpAsync(Phone, OtpPurpose.Login);
        Assert.Single(sms.Messages);
    }

    [Fact]
    public async Task PhoneVerification_NumberVerifiedOnAnotherAccount_IsRejected()
    {
        var (ctx, sms, otp) = Setup();
        await AddUserAsync(ctx, "victim@example.com", Phone, verified: true);
        var attacker = await AddUserAsync(ctx, "attacker@example.com", Phone, verified: false);

        Assert.Equal(PhoneVerificationSendResult.AlreadyVerifiedElsewhere, await otp.SendPhoneVerificationAsync(attacker));
        Assert.Empty(sms.Messages);
        Assert.Equal(PhoneVerificationConfirmResult.AlreadyVerifiedElsewhere, await otp.ConfirmPhoneVerificationAsync(attacker, "123456"));
        Assert.Null(attacker.VerifiedPhoneNumber);
    }

    [Fact]
    public async Task PhoneVerification_NumberWithoutCountryCode_IsRejected()
    {
        var (ctx, sms, otp) = Setup();
        var user = await AddUserAsync(ctx, "a@example.com", "0600000002", verified: false);

        Assert.Equal(PhoneVerificationSendResult.InvalidNumber, await otp.SendPhoneVerificationAsync(user));
        Assert.Empty(sms.Messages);
    }

    [Fact]
    public void Database_EnforcesOneVerifiedNumberPerAccount_WithAFilteredUniqueIndex()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var ctx = new IAMDbContext(options);
        var index = ctx.Model.FindEntityType(typeof(User))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(User.VerifiedPhoneNumber) }));

        Assert.True(index.IsUnique);
        Assert.Contains("IS NOT NULL", index.GetFilter());
    }

    private sealed class CapturingSms : ISmsService
    {
        public List<string> Messages { get; } = new();
        public Task<bool> SendSmsAsync(string phoneNumber, string message)
        {
            Messages.Add(message);
            return Task.FromResult(true);
        }
        public string LastCode() => Regex.Match(Messages.Last(), @"\d{6}").Value;
    }

    private sealed class NullEmail : IEmailService
    {
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.FromResult(true);
        public Task<bool> SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
    }
}
