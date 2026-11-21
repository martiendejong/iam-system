using System.Security.Claims;
using Fido2NetLib;
using Fido2NetLib.Objects;
using IAM.API.Auth;
using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using IAM.API.Tests.Services;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5488: disabling e-mail 2FA, regenerating recovery codes and starting a passkey registration need a fresh
/// proof of identity (current password or an e-mailed one-time code), not just a valid access token.
/// Drives the real controllers, SensitiveActionGuard and OtpService on an in-memory database.
/// </summary>
public class SensitiveActionConfirmationTests
{
    private const string Password = "Password123!";

    private sealed class Mailbox : IEmailService
    {
        public List<string> Codes { get; } = new();
        public List<(string To, string Subject)> Notices { get; } = new();
        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) { Codes.Add(code); return Task.CompletedTask; }
        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task<bool> SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.FromResult(true);
        public Task<bool> SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) { Notices.Add((email, subject)); return Task.FromResult(true); }
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class NoSms : ISmsService
    {
        public Task<bool> SendSmsAsync(string phoneNumber, string message) => Task.FromResult(true);
    }

    private sealed class Harness
    {
        public required IAMDbContext Context { get; init; }
        public required Mailbox Mail { get; init; }
        public required User User { get; init; }
        public required MfaController Mfa { get; init; }
        public required PasskeyController Passkey { get; init; }
        public required MethodStub PasskeyStub { get; init; }
        public required MethodStub TotpStub { get; init; }
        public int RecoveryGenerations;
        public int PasskeyBegins;
    }

    private static Harness Create(bool emailConfirmed = true, bool emailMfa = true)
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var context = new IAMDbContext(options);
        var user = new User
        {
            Email = "owner@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "Own",
            LastName = "Er",
            EmailConfirmed = emailConfirmed,
            IsActive = true,
            TwoFactorEnabled = emailMfa,
            TwoFactorMethod = emailMfa ? TwoFactorMethod.Email : TwoFactorMethod.None
        };
        context.Users.Add(user);
        context.SaveChanges();

        var mail = new Mailbox();
        var otp = new OtpService(context, mail, new NoSms(), Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" }), NullLogger<OtpService>.Instance);
        var guard = new SensitiveActionGuard(context, otp, mail, NullLogger<SensitiveActionGuard>.Instance);

        var (totp, totpStub) = MethodStub.New<ITotpService>();
        var (passkeyService, passkeyStub) = MethodStub.New<IPasskeyService>();
        var (authService, _) = MethodStub.New<IAuthService>();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("sub", user.Id.ToString())
        }, "test"));
        ControllerContext Ctx() => new() { HttpContext = new DefaultHttpContext { User = principal } };

        var h = new Harness
        {
            Context = context,
            Mail = mail,
            User = user,
            Mfa = new MfaController(totp, context, guard) { ControllerContext = Ctx() },
            Passkey = new PasskeyController(passkeyService, authService, context, NullLogger<PasskeyController>.Instance, guard) { ControllerContext = Ctx() },
            PasskeyStub = passkeyStub,
            TotpStub = totpStub
        };
        totpStub.On(nameof(ITotpService.GenerateRecoveryCodesAsync), _ => { h.RecoveryGenerations++; return Task.FromResult(new List<string> { "AAAA-BBBB" }); });
        passkeyStub.On(nameof(IPasskeyService.BeginRegistrationAsync), _ => { h.PasskeyBegins++; return Task.FromResult((CredentialCreateOptions)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CredentialCreateOptions))); });
        passkeyStub.On(nameof(IPasskeyService.CompleteRegistrationAsync), _ => Task.FromResult(true));
        return h;
    }

    private static int Status(IActionResult? result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 0
    };

    private static int Status<T>(ActionResult<T> result) => result.Result is null ? 200 : Status(result.Result);

    private static async Task<string> EmailCodeAsync(Harness h)
    {
        var sent = await h.Mfa.SendConfirmationCode(CancellationToken.None);
        Assert.Equal(200, Status(sent));
        return h.Mail.Codes.Last();
    }

    // ----- e-mail 2FA disable -----

    [Fact]
    public async Task EmailDisable_WithoutConfirmation_IsRefusedAndNothingChanges()
    {
        var h = Create();
        Assert.Equal(403, Status(await h.Mfa.DisableEmailMfa(null, CancellationToken.None)));
        Assert.Equal(403, Status(await h.Mfa.DisableEmailMfa(new SensitiveActionRequest(), CancellationToken.None)));

        var user = await h.Context.Users.AsNoTracking().SingleAsync();
        Assert.True(user.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.Email, user.TwoFactorMethod);
        Assert.Empty(h.Mail.Notices);
        Assert.Empty(h.Context.AuditLogs);
    }

    [Fact]
    public async Task EmailDisable_WrongPassword_IsRefusedAndCountsTowardLockout()
    {
        var h = Create();
        for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
            Assert.Equal(403, Status(await h.Mfa.DisableEmailMfa(new SensitiveActionRequest("wrong"), CancellationToken.None)));

        var user = await h.Context.Users.AsNoTracking().SingleAsync();
        Assert.True(user.IsLockedOut);
        Assert.True(user.TwoFactorEnabled);

        // Locked: even the right password is refused (423) and 2FA stays on.
        Assert.Equal(423, Status(await h.Mfa.DisableEmailMfa(new SensitiveActionRequest(Password), CancellationToken.None)));
        Assert.True((await h.Context.Users.AsNoTracking().SingleAsync()).TwoFactorEnabled);
    }

    [Fact]
    public async Task EmailDisable_CorrectPassword_DisablesAuditsAndNotifiesOwner()
    {
        var h = Create();
        h.User.FailedLoginAttempts = 2;
        await h.Context.SaveChangesAsync();

        Assert.Equal(200, Status(await h.Mfa.DisableEmailMfa(new SensitiveActionRequest(Password), CancellationToken.None)));

        var user = await h.Context.Users.AsNoTracking().SingleAsync();
        Assert.False(user.TwoFactorEnabled);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Contains(h.Context.AuditLogs, a => a.Action == "EmailMfaDisabled" && a.UserId == user.Id);
        Assert.Single(h.Mail.Notices);
        Assert.Equal("owner@example.com", h.Mail.Notices[0].To);
    }

    [Fact]
    public async Task EmailDisable_EmailedCode_WorksOnceThenIsRefused()
    {
        var h = Create();
        var code = await EmailCodeAsync(h);

        Assert.Equal(200, Status(await h.Mfa.DisableEmailMfa(new SensitiveActionRequest(null, code), CancellationToken.None)));

        // Reuse of the same code on another protected action is refused.
        Assert.Equal(403, Status(await h.Mfa.GenerateRecoveryCodes(new SensitiveActionRequest(null, code), CancellationToken.None)));
        Assert.Equal(0, h.RecoveryGenerations);
    }

    [Fact]
    public async Task EmailDisable_ExpiredOrWrongCode_IsRefused()
    {
        var h = Create();
        var code = await EmailCodeAsync(h);

        Assert.Equal(403, Status(await h.Mfa.DisableEmailMfa(new SensitiveActionRequest(null, "000000" == code ? "111111" : "000000"), CancellationToken.None)));

        foreach (var otp in h.Context.OtpCodes) otp.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await h.Context.SaveChangesAsync();

        Assert.Equal(403, Status(await h.Mfa.DisableEmailMfa(new SensitiveActionRequest(null, code), CancellationToken.None)));
        Assert.True((await h.Context.Users.AsNoTracking().SingleAsync()).TwoFactorEnabled);
    }

    [Fact]
    public async Task ConfirmationCode_IsNotSentToAnUnverifiedAddress()
    {
        var h = Create(emailConfirmed: false);
        Assert.Equal(400, Status(await h.Mfa.SendConfirmationCode(CancellationToken.None)));
        Assert.Empty(h.Mail.Codes);
    }

    [Fact]
    public async Task LoginCode_CannotBeUsedAsConfirmation()
    {
        var h = Create();
        var otp = new OtpService(h.Context, h.Mail, new NoSms(), Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" }), NullLogger<OtpService>.Instance);
        await otp.SendEmailOtpAsync(h.User.Email, OtpPurpose.MfaVerification);
        var loginCode = h.Mail.Codes.Last();

        Assert.Equal(403, Status(await h.Mfa.DisableEmailMfa(new SensitiveActionRequest(null, loginCode), CancellationToken.None)));
    }

    // ----- recovery codes -----

    [Fact]
    public async Task RecoveryCodes_WithoutConfirmationOrWrongPassword_AreNotRegenerated()
    {
        var h = Create();
        Assert.Equal(403, Status(await h.Mfa.GenerateRecoveryCodes(null, CancellationToken.None)));
        Assert.Equal(403, Status(await h.Mfa.GenerateRecoveryCodes(new SensitiveActionRequest("nope"), CancellationToken.None)));
        Assert.Equal(0, h.RecoveryGenerations);
        Assert.Empty(h.Mail.Notices);
    }

    [Fact]
    public async Task RecoveryCodes_CorrectPassword_RegeneratesAuditsAndNotifies()
    {
        var h = Create();
        Assert.Equal(200, Status(await h.Mfa.GenerateRecoveryCodes(new SensitiveActionRequest(Password), CancellationToken.None)));
        Assert.Equal(1, h.RecoveryGenerations);
        Assert.Contains(h.Context.AuditLogs, a => a.Action == "RecoveryCodesRegenerated");
        Assert.Single(h.Mail.Notices);
    }

    [Fact]
    public async Task RecoveryCodes_EmailedCode_WorksForAccountWithoutKnownPassword()
    {
        var h = Create(emailMfa: false);
        var code = await EmailCodeAsync(h);
        Assert.Equal(200, Status(await h.Mfa.GenerateRecoveryCodes(new SensitiveActionRequest(null, code), CancellationToken.None)));
        Assert.Equal(1, h.RecoveryGenerations);
    }

    // ----- passkey registration -----

    private static BeginRegistrationRequest Begin(string? password = null, string? code = null) =>
        new("owner@example.com", "Own Er", password, code);

    [Fact]
    public async Task PasskeyBegin_WithoutConfirmationOrWrongPassword_IsRefusedAndNoChallengeIssued()
    {
        var h = Create();
        Assert.Equal(403, Status(await h.Passkey.BeginRegistration(Begin(), CancellationToken.None)));
        Assert.Equal(403, Status(await h.Passkey.BeginRegistration(Begin("nope"), CancellationToken.None)));
        Assert.Equal(0, h.PasskeyBegins);
    }

    [Fact]
    public async Task PasskeyBegin_LockedAccount_IsRefused()
    {
        var h = Create();
        h.User.IsLockedOut = true;
        h.User.LockoutEnd = DateTime.UtcNow.AddMinutes(10);
        await h.Context.SaveChangesAsync();

        Assert.Equal(423, Status(await h.Passkey.BeginRegistration(Begin(Password), CancellationToken.None)));
        Assert.Equal(0, h.PasskeyBegins);
    }

    [Fact]
    public async Task PasskeyBegin_CorrectPasswordOrCode_StartsRegistration_AndCompleteAuditsAndNotifies()
    {
        var h = Create();
        Assert.Equal(200, Status(await h.Passkey.BeginRegistration(Begin(Password), CancellationToken.None)));

        var code = await EmailCodeAsync(h);
        Assert.Equal(200, Status(await h.Passkey.BeginRegistration(Begin(null, code), CancellationToken.None)));
        Assert.Equal(2, h.PasskeyBegins);
        // The same code cannot start a second registration.
        Assert.Equal(403, Status(await h.Passkey.BeginRegistration(Begin(null, code), CancellationToken.None)));

        var done = await h.Passkey.CompleteRegistration(
            new CompleteRegistrationRequest("laptop", new AuthenticatorAttestationRawResponse()), CancellationToken.None);
        Assert.Equal(200, Status(done));
        Assert.Contains(h.Context.AuditLogs, a => a.Action == "PasskeyRegistered");
        Assert.Single(h.Mail.Notices);
    }
}
