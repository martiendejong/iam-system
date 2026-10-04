using System.Security.Cryptography;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 3162: TOTP moved from HMAC-SHA1 to HMAC-SHA256 (PR #104), so an authenticator app enrolled before it can
/// never produce an accepted code. Decision: those members move to e-mail PIN two-factor at their next sign-in,
/// and each tenant can switch that off (LegacyTotpMigrationMode.Off).
/// </summary>
public class LegacyTotpMigrationTests
{
    private const string Password = "Password123!";

    // ----- harness ----------------------------------------------------------------------------

    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IAMDbContext(options);
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        }).Build();

    private static (AuthService auth, FakeEmailService email, IAMDbContext context) CreateServices()
    {
        var context = CreateContext();
        var email = new FakeEmailService();
        var otp = new OtpService(context, email, new FakeSmsService(),
            Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" }), NullLogger<OtpService>.Instance);
        var risk = new RiskAssessmentService(context, NullLogger<RiskAssessmentService>.Instance);
        var auth = new AuthService(context, CreateConfiguration(), email, risk, otp,
            new ClaimsMappingService(context), NullLogger<AuthService>.Instance);
        return (auth, email, context);
    }

    private static User NewUser(
        TwoFactorMethod method,
        string? totpAlgorithm = null,
        bool emailConfirmed = true,
        string? secret = null)
    {
        return new User
        {
            Email = $"{Guid.NewGuid():N}@legacy-totp.test",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = emailConfirmed,
            IsActive = true,
            TwoFactorEnabled = method != TwoFactorMethod.None,
            TwoFactorMethod = method,
            TwoFactorSecret = method == TwoFactorMethod.Totp ? secret ?? "JBSWY3DPEHPK3PXP" : null,
            TotpAlgorithm = totpAlgorithm
        };
    }

    private static async Task<User> SeedLegacyTotpUserAsync(IAMDbContext context, int recoveryCodes = 2)
    {
        var user = NewUser(TwoFactorMethod.Totp);
        context.Users.Add(user);
        for (var i = 0; i < recoveryCodes; i++)
        {
            context.RecoveryCodes.Add(new RecoveryCode { UserId = user.Id, CodeHash = $"hash-{i}" });
        }
        await context.SaveChangesAsync();
        return user;
    }

    /// <summary>Gives the user a role row scoped to a fresh tenant, with an optional settings row.</summary>
    private static async Task<Tenant> PlaceInTenantAsync(
        IAMDbContext context, User user, LegacyTotpMigrationMode? mode)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        var role = new Role { Id = Guid.NewGuid(), Name = $"Member {Guid.NewGuid():N}", TenantId = tenant.Id, Permissions = "[]" };
        context.Tenants.Add(tenant);
        context.Roles.Add(role);
        context.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id, TenantId = tenant.Id, GrantedAt = DateTime.UtcNow
        });
        if (mode.HasValue)
        {
            context.OrganizationSettings.Add(new OrganizationSettings
            {
                Id = Guid.NewGuid(), TenantId = tenant.Id, LegacyTotpMigration = mode.Value
            });
        }
        await context.SaveChangesAsync();
        return tenant;
    }

    // RFC 6238 with an explicit hash, so the tests can build both a legacy (SHA-1) and a current (SHA-256) code.
    private static string Code(string base32Secret, bool sha1)
    {
        var key = Base32Decode(base32Secret);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var message = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            message[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        var hash = sha1 ? new HMACSHA1(key).ComputeHash(message) : new HMACSHA256(key).ComputeHash(message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in value.Replace(" ", "").ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return bytes.ToArray();
    }

    // ----- the migration at sign-in -----------------------------------------------------------

    [Fact]
    public async Task Login_LegacyTotpUser_IsMovedToEmailPinAndChallengedWithAPin()
    {
        var (auth, email, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.True(result.Success);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);

        Assert.NotNull(result.LegacyTotpMigration);
        Assert.Equal("totp", result.LegacyTotpMigration!.FromMethod);
        Assert.Equal("email", result.LegacyTotpMigration.ToMethod);
        Assert.False(string.IsNullOrWhiteSpace(result.LegacyTotpMigration.Message));

        var sent = Assert.Single(email.SentTwoFactorCodes);
        Assert.Equal(user.Email, sent.Email);

        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.True(stored.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.Email, stored.TwoFactorMethod);
        Assert.Null(stored.TwoFactorSecret);
        Assert.Null(stored.TotpAlgorithm);
        Assert.False(stored.HasLegacyTotpEnrollment());
        Assert.Empty(await context.RecoveryCodes.Where(rc => rc.UserId == user.Id).ToListAsync());

        var audit = Assert.Single(await context.AuditLogs.Where(a => a.UserId == user.Id && a.Action == "LegacyTotpMigratedToEmailPin").ToListAsync());
        Assert.Equal("User", audit.Resource);
    }

    [Fact]
    public async Task Login_LegacyTotpUser_PinFromTheEmailCompletesTheLogin()
    {
        var (auth, email, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);

        var login = await auth.LoginAsync(user.Email, Password);
        Assert.True(login.RequiresTwoFactor);

        var verified = await auth.VerifyLoginTwoFactorAsync(user.Id, email.SentTwoFactorCodes[0].Code);

        Assert.True(verified.Success);
        Assert.NotNull(verified.AccessToken);
        Assert.Null(verified.LegacyTotpMigration);
    }

    [Fact]
    public async Task Login_LegacyTotpUser_WrongPasswordMigratesNothing()
    {
        var (auth, email, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);

        var result = await auth.LoginAsync(user.Email, "not-the-password");

        Assert.False(result.Success);
        Assert.Null(result.LegacyTotpMigration);
        Assert.Empty(email.SentTwoFactorCodes);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(TwoFactorMethod.Totp, stored.TwoFactorMethod);
        Assert.NotNull(stored.TwoFactorSecret);
        Assert.Equal(2, await context.RecoveryCodes.CountAsync(rc => rc.UserId == user.Id));
    }

    [Fact]
    public async Task Login_MigratesOnlyOnce_NextLoginIsAPlainEmailPinLogin()
    {
        var (auth, email, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);

        var first = await auth.LoginAsync(user.Email, Password);
        var second = await auth.LoginAsync(user.Email, Password);

        Assert.NotNull(first.LegacyTotpMigration);
        Assert.True(second.RequiresTwoFactor);
        Assert.Null(second.LegacyTotpMigration);
        Assert.Equal(2, email.SentTwoFactorCodes.Count);
        Assert.Single(await context.AuditLogs.Where(a => a.Action == "LegacyTotpMigratedToEmailPin").ToListAsync());
    }

    [Fact]
    public async Task Login_Sha256TotpUser_IsNotMigrated()
    {
        var (auth, email, context) = CreateServices();
        var user = NewUser(TwoFactorMethod.Totp, totpAlgorithm: TotpAlgorithms.Sha256);
        context.Users.Add(user);
        context.RecoveryCodes.Add(new RecoveryCode { UserId = user.Id, CodeHash = "hash" });
        await context.SaveChangesAsync();

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.Null(result.LegacyTotpMigration);
        Assert.Empty(email.SentTwoFactorCodes);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(TwoFactorMethod.Totp, stored.TwoFactorMethod);
        Assert.NotNull(stored.TwoFactorSecret);
        Assert.Equal(TotpAlgorithms.Sha256, stored.TotpAlgorithm);
        Assert.Equal(1, await context.RecoveryCodes.CountAsync(rc => rc.UserId == user.Id));
    }

    [Theory]
    [InlineData(TwoFactorMethod.None)]
    [InlineData(TwoFactorMethod.Email)]
    public async Task Login_UsersWithoutAnAuthenticatorEnrollment_AreNotTouched(TwoFactorMethod method)
    {
        var (auth, _, context) = CreateServices();
        var user = NewUser(method);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.Null(result.LegacyTotpMigration);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(method, stored.TwoFactorMethod);
        Assert.Empty(await context.AuditLogs.Where(a => a.Action == "LegacyTotpMigratedToEmailPin").ToListAsync());
    }

    // ----- tenant policy ----------------------------------------------------------------------

    [Fact]
    public async Task Policy_TenantWithoutSettingsRow_DefaultsToEmailPin()
    {
        var (auth, _, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);
        await PlaceInTenantAsync(context, user, mode: null);

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.NotNull(result.LegacyTotpMigration);
    }

    [Fact]
    public async Task Policy_TenantSetToOff_LeavesTheLegacyEnrollmentUntouched()
    {
        var (auth, email, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);
        await PlaceInTenantAsync(context, user, LegacyTotpMigrationMode.Off);

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.Null(result.LegacyTotpMigration);
        Assert.Empty(email.SentTwoFactorCodes);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(TwoFactorMethod.Totp, stored.TwoFactorMethod);
        Assert.NotNull(stored.TwoFactorSecret);
        Assert.True(stored.HasLegacyTotpEnrollment());
        Assert.Equal(2, await context.RecoveryCodes.CountAsync(rc => rc.UserId == user.Id));
    }

    [Fact]
    public async Task Policy_TenantSetToEmailPin_Migrates()
    {
        var (auth, _, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);
        await PlaceInTenantAsync(context, user, LegacyTotpMigrationMode.EmailPin);

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.NotNull(result.LegacyTotpMigration);
    }

    [Fact]
    public async Task Policy_MemberOfSeveralTenants_IsLeftAloneOnlyWhenEveryTenantOptsOut()
    {
        var (_, _, context) = CreateServices();

        var optedOutEverywhere = await SeedLegacyTotpUserAsync(context);
        await PlaceInTenantAsync(context, optedOutEverywhere, LegacyTotpMigrationMode.Off);
        await PlaceInTenantAsync(context, optedOutEverywhere, LegacyTotpMigrationMode.Off);

        var oneTenantStillMigrates = await SeedLegacyTotpUserAsync(context);
        await PlaceInTenantAsync(context, oneTenantStillMigrates, LegacyTotpMigrationMode.Off);
        await PlaceInTenantAsync(context, oneTenantStillMigrates, LegacyTotpMigrationMode.EmailPin);

        var oneTenantWithoutSettings = await SeedLegacyTotpUserAsync(context);
        await PlaceInTenantAsync(context, oneTenantWithoutSettings, LegacyTotpMigrationMode.Off);
        await PlaceInTenantAsync(context, oneTenantWithoutSettings, mode: null);

        Assert.Equal(LegacyTotpMigrationMode.Off, await LegacyTotpMigration.ResolveModeAsync(context, optedOutEverywhere.Id));
        Assert.Equal(LegacyTotpMigrationMode.EmailPin, await LegacyTotpMigration.ResolveModeAsync(context, oneTenantStillMigrates.Id));
        Assert.Equal(LegacyTotpMigrationMode.EmailPin, await LegacyTotpMigration.ResolveModeAsync(context, oneTenantWithoutSettings.Id));
    }

    [Fact]
    public async Task Policy_AnotherTenantsOptOutDoesNotApply()
    {
        var (auth, _, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);
        await PlaceInTenantAsync(context, user, mode: null);

        // A different tenant (the user has no role there) opts out.
        var other = new Tenant { Id = Guid.NewGuid(), Name = "Other", IsActive = true };
        context.Tenants.Add(other);
        context.OrganizationSettings.Add(new OrganizationSettings { TenantId = other.Id, LegacyTotpMigration = LegacyTotpMigrationMode.Off });
        await context.SaveChangesAsync();

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.NotNull(result.LegacyTotpMigration);
    }

    // ----- only a password sign-in migrates ---------------------------------------------------

    [Fact]
    public async Task Passwordless_MailboxProofAlone_NeverStripsAnAuthenticatorEnrollment()
    {
        // A magic link / OTP proves only mailbox access and the PIN would go to that same mailbox: the migration
        // must wait for a password sign-in, where the password is a separate factor.
        var (auth, email, context) = CreateServices();
        var user = await SeedLegacyTotpUserAsync(context);

        var result = await auth.CompletePasswordlessLoginAsync(user);

        Assert.Null(result.LegacyTotpMigration);
        Assert.Empty(email.SentTwoFactorCodes);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(TwoFactorMethod.Totp, stored.TwoFactorMethod);
        Assert.NotNull(stored.TwoFactorSecret);
        Assert.Equal(2, await context.RecoveryCodes.CountAsync(rc => rc.UserId == user.Id));
        Assert.Empty(await context.AuditLogs.Where(a => a.Action == "LegacyTotpMigratedToEmailPin").ToListAsync());

        // ... and the member is not stranded: the next password sign-in migrates them.
        var passwordLogin = await auth.LoginAsync(user.Email, Password);
        Assert.NotNull(passwordLogin.LegacyTotpMigration);
        Assert.Single(email.SentTwoFactorCodes);
    }

    [Fact]
    public async Task Login_UnconfirmedEmail_IsRefusedBeforeAnythingIsMigrated()
    {
        var (auth, email, context) = CreateServices();
        var user = NewUser(TwoFactorMethod.Totp, emailConfirmed: false);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await auth.LoginAsync(user.Email, Password);

        Assert.False(result.Success);
        Assert.Null(result.LegacyTotpMigration);
        Assert.Empty(email.SentTwoFactorCodes);
        Assert.Equal(TwoFactorMethod.Totp, (await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).TwoFactorMethod);
    }

    // ----- what counts as a legacy enrollment -------------------------------------------------

    [Theory]
    [InlineData(TwoFactorMethod.Totp, null, true, true)]
    [InlineData(TwoFactorMethod.Totp, "SHA1", true, true)]
    [InlineData(TwoFactorMethod.Totp, "SHA256", true, false)]
    [InlineData(TwoFactorMethod.Totp, null, false, false)] // setup started, never activated
    [InlineData(TwoFactorMethod.Email, null, true, false)]
    [InlineData(TwoFactorMethod.None, null, false, false)]
    public async Task HasLegacyTotpEnrollment_MatchesTheQueryForm(
        TwoFactorMethod method, string? algorithm, bool enabled, bool expected)
    {
        var context = CreateContext();
        var user = NewUser(method, algorithm);
        user.TwoFactorEnabled = enabled;
        context.Users.Add(user);
        await context.SaveChangesAsync();

        Assert.Equal(expected, user.HasLegacyTotpEnrollment());
        Assert.Equal(expected, await context.Users.Where(User.LegacyTotpEnrollment).AnyAsync(u => u.Id == user.Id));
    }

    // ----- TotpService keeps the marker honest ------------------------------------------------

    private static TotpService CreateTotpService(IAMDbContext context) => new(context);

    [Fact]
    public async Task Activating_ANewEnrollment_RecordsSha256()
    {
        var context = CreateContext();
        var totp = CreateTotpService(context);
        var user = NewUser(TwoFactorMethod.None);
        user.EmailConfirmed = true;
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var setup = await totp.EnableTotpAsync(user.Id);
        var activated = await totp.VerifyAndActivateTotpAsync(user.Id, Code(setup.Secret, sha1: false));

        Assert.True(activated);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(TotpAlgorithms.Sha256, stored.TotpAlgorithm);
        Assert.False(stored.HasLegacyTotpEnrollment());
    }

    [Fact]
    public async Task ValidatingASha256Code_ForAnUnmarkedEnrollment_StampsItSha256()
    {
        // Enrolled after PR #104 but before TotpAlgorithm existed: the SHA-256 code proves it is not legacy.
        var context = CreateContext();
        var totp = CreateTotpService(context);
        var user = NewUser(TwoFactorMethod.Totp, totpAlgorithm: null);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        Assert.True(user.HasLegacyTotpEnrollment());

        var ok = await totp.ValidateTotpLoginAsync(user.Id, Code(user.TwoFactorSecret!, sha1: false));

        Assert.True(ok);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(TotpAlgorithms.Sha256, stored.TotpAlgorithm);
        Assert.False(stored.HasLegacyTotpEnrollment());
    }

    [Fact]
    public async Task ValidatingALegacySha1Code_IsRejectedAndTheEnrollmentStaysLegacy()
    {
        var context = CreateContext();
        var totp = CreateTotpService(context);
        var user = NewUser(TwoFactorMethod.Totp, totpAlgorithm: null);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var sha1Code = Code(user.TwoFactorSecret!, sha1: true);
        // Guard against the 1-in-a-million case where both hashes yield the same 6 digits at this instant.
        if (sha1Code == Code(user.TwoFactorSecret!, sha1: false))
            return;

        var ok = await totp.ValidateTotpLoginAsync(user.Id, sha1Code);

        Assert.False(ok);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Null(stored.TotpAlgorithm);
        Assert.True(stored.HasLegacyTotpEnrollment());
    }

    [Fact]
    public async Task Disabling_Totp_ClearsTheAlgorithmMarker()
    {
        var context = CreateContext();
        var totp = CreateTotpService(context);
        var user = NewUser(TwoFactorMethod.Totp, totpAlgorithm: TotpAlgorithms.Sha256);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var disabled = await totp.DisableTotpAsync(user.Id, Code(user.TwoFactorSecret!, sha1: false));

        Assert.True(disabled);
        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Null(stored.TotpAlgorithm);
        Assert.Null(stored.TwoFactorSecret);
        Assert.Equal(TwoFactorMethod.None, stored.TwoFactorMethod);
    }

    // ----- fakes ------------------------------------------------------------------------------

    private class FakeEmailService : IEmailService
    {
        public List<(string Email, string Code)> SentTwoFactorCodes { get; } = new();

        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default)
        {
            SentTwoFactorCodes.Add((email, code));
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
