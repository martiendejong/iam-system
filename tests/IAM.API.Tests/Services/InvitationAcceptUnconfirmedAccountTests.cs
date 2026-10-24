using Hazina.Security.ApiKeys;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5158: an attacker can register an account for someone else's e-mail address (a social login whose provider
/// does not verify addresses) and it stays unconfirmed. When a manager later invites that address, accepting the
/// invitation used to hand the tenant role to that account, which kept the attacker's social sign-in. Now accepting
/// for an existing account whose e-mail is NOT confirmed first cleans it (social logins, passkeys, refresh and OAuth
/// tokens, other sign-in methods, old password), requires a new password, and only then confirms the e-mail and grants
/// the role, all in one save. Confirmed existing accounts and brand-new accounts behave as before.
/// </summary>
public class InvitationAcceptUnconfirmedAccountTests
{
    private const string Email = "victim@invited.test";
    private const string AttackerPassword = "Attacker-Pass-1!";
    private const string NewPassword = "Owner-New-Pass-2!";

    private sealed class Scene
    {
        public required DbContextOptions<IAMDbContext> Options { get; init; }
        public required Tenant Tenant { get; init; }
        public required Role Role { get; init; }
        public required User Sender { get; init; }
        public required Invitation Invitation { get; init; }

        public IAMDbContext NewContext() => new(Options);
    }

    private static Scene CreateScene(string email = Email)
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var context = new IAMDbContext(options);

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        var role = new Role { Id = Guid.NewGuid(), Name = "Resident", Permissions = "[\"Room.View\"]" };
        var sender = new User { Email = $"{Guid.NewGuid():N}@sender.test", PasswordHash = "not-used", EmailConfirmed = true, IsActive = true };
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            Email = email,
            TenantId = tenant.Id,
            RoleId = role.Id,
            Token = Guid.NewGuid().ToString("N"),
            Status = "Pending",
            InvitedByUserId = sender.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(3)
        };
        context.AddRange(tenant, role, sender, invitation);
        context.SaveChanges();

        return new Scene { Options = options, Tenant = tenant, Role = role, Sender = sender, Invitation = invitation };
    }

    private static InvitationService CreateService(IAMDbContext context, IApiKeyCache? apiKeyCache = null) =>
        new(context, new RecordingEmailService(), new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<InvitationService>.Instance, apiKeyCache);

    /// <summary>Stands in for the key module's cache, which keeps validated keys for a couple of minutes.</summary>
    private sealed class RecordingApiKeyCache : IApiKeyCache
    {
        public List<string> Invalidated { get; } = new();
        public void Invalidate(string keyHash) => Invalidated.Add(keyHash);
        public void InvalidateAll() => throw new InvalidOperationException("Only the revoked keys should be dropped from the cache");
    }

    private static IConfiguration JwtConfig() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
        ["Jwt:Issuer"] = "iam-tests",
        ["Jwt:Audience"] = "iam-tests",
        ["Jwt:AccessTokenExpirationMinutes"] = "5"
    }).Build();

    /// <summary>The account the attacker registered for the victim's address, with every kind of access it can hold.</summary>
    private static User SeedAttackerAccount(Scene scene, bool emailConfirmed = false)
    {
        using var context = scene.NewContext();
        var user = new User
        {
            Email = scene.Invitation.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(AttackerPassword, workFactor: 4),
            FirstName = "Mallory",
            LastName = "Attacker",
            PhoneNumber = "+31600000000",
            AvatarUrl = "https://attacker.example/avatar.png",
            EmailConfirmed = emailConfirmed,
            EmailVerificationToken = "verify-token",
            EmailVerificationTokenExpiry = DateTime.UtcNow.AddDays(1),
            PasswordResetToken = "reset-token",
            PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1),
            TwoFactorEnabled = true,
            TwoFactorSecret = "ATTACKERTOTPSECRET",
            TwoFactorMethod = TwoFactorMethod.Totp,
            FailedLoginAttempts = 3,
            IsLockedOut = true,
            LockoutEnd = DateTime.UtcNow.AddMinutes(10),
            IsActive = true
        };
        context.Users.Add(user);
        context.SaveChanges();

        var subject = user.Id.ToString();
        context.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = "Microsoft", ProviderUserId = "attacker-ms-id", Email = scene.Invitation.Email });
        context.Credentials.Add(new Credential
        {
            Id = Guid.NewGuid(), UserId = user.Id, CredentialId = new byte[] { 1, 2, 3 }, PublicKey = new byte[] { 4, 5, 6 },
            CredType = "public-key", AaGuid = Guid.NewGuid(), CreatedAt = DateTime.UtcNow
        });
        context.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = "attacker-refresh", ExpiresAt = DateTime.UtcNow.AddDays(7) });
        context.RecoveryCodes.Add(new RecoveryCode { Id = Guid.NewGuid(), UserId = user.Id, CodeHash = "hash" });
        context.TrustedDevices.Add(new TrustedDevice { UserId = user.Id, DeviceFingerprint = "fp", Name = "Attacker laptop" });
        context.MagicLinkTokens.Add(new MagicLinkToken { UserId = user.Id, Token = "magic", ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        context.OtpCodes.Add(new OtpCode { UserId = user.Id, Code = "123456", Purpose = OtpPurpose.Login, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        context.OtpCodes.Add(new OtpCode { PhoneNumber = "+31600000000", Code = "654321", Purpose = OtpPurpose.Login, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        context.ApiKeys.Add(new ApiKey { Id = Guid.NewGuid(), Name = "attacker key", KeyHash = "keyhash", UserId = user.Id, IsActive = true });
        context.UserSessions.Add(new UserSession { UserId = user.Id, SessionToken = "session", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        context.Set<OpenIddictEntityFrameworkCoreToken>().Add(new OpenIddictEntityFrameworkCoreToken
        {
            Id = Guid.NewGuid().ToString(), Subject = subject, Status = "valid", Type = "refresh_token"
        });
        context.Set<OpenIddictEntityFrameworkCoreAuthorization>().Add(new OpenIddictEntityFrameworkCoreAuthorization
        {
            Id = Guid.NewGuid().ToString(), Subject = subject, Status = "valid", Type = "permanent"
        });
        context.SaveChanges();
        return user;
    }

    private static async Task<InvitationAcceptResult> AcceptAsync(Scene scene, string? password, string? first = "Real", string? last = "Owner")
    {
        using var context = scene.NewContext();
        return await CreateService(context).AcceptInvitationAsync(scene.Invitation.Token, password, first, last);
    }

    private static async Task AssertAccountUntouchedAsync(Scene scene, User original)
    {
        using var read = scene.NewContext();
        var user = await read.Users.AsNoTracking().SingleAsync(u => u.Id == original.Id);
        Assert.False(user.EmailConfirmed);
        Assert.Equal(original.PasswordHash, user.PasswordHash);
        Assert.Equal("Mallory", user.FirstName);
        Assert.True(user.TwoFactorEnabled);
        Assert.Equal("+31600000000", user.PhoneNumber);
        Assert.Equal(1, await read.ExternalLogins.CountAsync(e => e.UserId == user.Id));
        Assert.Equal(1, await read.Credentials.CountAsync(c => c.UserId == user.Id));
        Assert.Equal(1, await read.RefreshTokens.CountAsync(t => t.UserId == user.Id));
        Assert.Equal(1, await read.RecoveryCodes.CountAsync(r => r.UserId == user.Id));
        Assert.Equal(1, await read.TrustedDevices.CountAsync(d => d.UserId == user.Id));
        Assert.Equal(1, await read.MagicLinkTokens.CountAsync(m => m.UserId == user.Id));
        Assert.Equal(2, await read.OtpCodes.CountAsync());
        Assert.True((await read.ApiKeys.SingleAsync(k => k.UserId == user.Id)).IsActive);
        Assert.False((await read.UserSessions.SingleAsync(s => s.UserId == user.Id)).IsRevoked);
        Assert.Equal("valid", (await read.Set<OpenIddictEntityFrameworkCoreToken>().SingleAsync()).Status);
        Assert.Equal("valid", (await read.Set<OpenIddictEntityFrameworkCoreAuthorization>().SingleAsync()).Status);
        Assert.Empty(await read.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync());
        Assert.Equal("Pending", (await read.Set<Invitation>().AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await read.AuditLogs.ToListAsync());
    }

    // ----- an unconfirmed existing account is cleaned, then confirmed and granted the role --------------

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_RemovesEverySignInMethodAndToken()
    {
        var scene = CreateScene();
        var attacker = SeedAttackerAccount(scene);

        var result = await AcceptAsync(scene, NewPassword);

        Assert.True(result.Success);
        Assert.Equal(attacker.Id, result.User!.Id); // the same account row, not a second one

        using var read = scene.NewContext();
        Assert.Empty(await read.ExternalLogins.ToListAsync());
        Assert.Empty(await read.Credentials.ToListAsync());
        Assert.Empty(await read.RefreshTokens.ToListAsync());
        Assert.Empty(await read.RecoveryCodes.ToListAsync());
        Assert.Empty(await read.TrustedDevices.ToListAsync());
        Assert.Empty(await read.MagicLinkTokens.ToListAsync());
        Assert.Empty(await read.OtpCodes.ToListAsync()); // the one for the account and the one for its old phone number
        Assert.False((await read.ApiKeys.SingleAsync()).IsActive);
        var session = await read.UserSessions.SingleAsync();
        Assert.True(session.IsRevoked);
        Assert.NotNull(session.RevokedAt);
        Assert.Equal("revoked", (await read.Set<OpenIddictEntityFrameworkCoreToken>().SingleAsync()).Status);
        Assert.Equal("revoked", (await read.Set<OpenIddictEntityFrameworkCoreAuthorization>().SingleAsync()).Status);

        var user = await read.Users.AsNoTracking().SingleAsync(u => u.Email == Email);
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.TwoFactorSecret);
        Assert.Equal(TwoFactorMethod.None, user.TwoFactorMethod);
        Assert.Null(user.PhoneNumber); // SMS one-time-code login finds the account by this number
        Assert.Null(user.AvatarUrl);
        Assert.Null(user.EmailVerificationToken);
        Assert.Null(user.PasswordResetToken);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.False(user.IsLockedOut);
        Assert.Null(user.LockoutEnd);
        Assert.Equal("Real", user.FirstName);
        Assert.Equal("Owner", user.LastName);
    }

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_RevokedApiKeyLeavesTheKeyCacheAtOnce()
    {
        var scene = CreateScene();
        SeedAttackerAccount(scene);
        var cache = new RecordingApiKeyCache();

        using (var context = scene.NewContext())
        {
            var result = await CreateService(context, cache).AcceptInvitationAsync(scene.Invitation.Token, NewPassword, "Real", "Owner");
            Assert.True(result.Success);
        }

        // Without this the attacker's key keeps authenticating from the cache until its two-minute lifetime ends.
        Assert.Equal(new[] { "keyhash" }, cache.Invalidated);
        using var read = scene.NewContext();
        Assert.False((await read.ApiKeys.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Accept_ConfirmedExistingAccount_LeavesTheKeyCacheAlone()
    {
        var scene = CreateScene();
        SeedAttackerAccount(scene, emailConfirmed: true);
        var cache = new RecordingApiKeyCache();

        using var context = scene.NewContext();
        Assert.True((await CreateService(context, cache).AcceptInvitationAsync(scene.Invitation.Token, null, null, null)).Success);

        Assert.Empty(cache.Invalidated);
    }

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_OldPasswordStopsWorking_NewOneWorks()
    {
        var scene = CreateScene();
        var attacker = SeedAttackerAccount(scene);

        Assert.True((await AcceptAsync(scene, NewPassword)).Success);

        using var context = scene.NewContext();
        var auth = AuthServiceTestFactory.Create(context, JwtConfig());
        var withOld = await auth.LoginAsync(Email, AttackerPassword);
        Assert.False(withOld.Success);
        var withNew = await auth.LoginAsync(Email, NewPassword);
        Assert.True(withNew.Success);
        Assert.False(withNew.RequiresTwoFactor); // the attacker's authenticator secret is gone too
        Assert.Equal(attacker.Id, withNew.User!.Id);
    }

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_ConfirmsEmail_AndGrantsTheRoleOnce()
    {
        var scene = CreateScene();
        var attacker = SeedAttackerAccount(scene);

        Assert.True((await AcceptAsync(scene, NewPassword)).Success);

        using var read = scene.NewContext();
        Assert.True((await read.Users.AsNoTracking().SingleAsync(u => u.Email == Email)).EmailConfirmed);
        var grants = await read.UserRoles.Where(ur => ur.UserId == attacker.Id).ToListAsync();
        var grant = Assert.Single(grants);
        Assert.Equal(scene.Role.Id, grant.RoleId);
        Assert.Equal(scene.Tenant.Id, grant.TenantId);
        var invitation = await read.Set<Invitation>().AsNoTracking().SingleAsync();
        Assert.Equal("Accepted", invitation.Status);
        Assert.NotNull(invitation.AcceptedAt);

        var audit = Assert.Single(await read.AuditLogs.ToListAsync());
        Assert.Equal("UnconfirmedAccountResetOnInvitation", audit.Action);
        Assert.Equal(attacker.Id, audit.UserId);
        Assert.Equal(scene.Tenant.Id, audit.TenantId);
    }

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_AttackersSocialLoginHasNoLinkLeft()
    {
        var scene = CreateScene();
        SeedAttackerAccount(scene);

        Assert.True((await AcceptAsync(scene, NewPassword)).Success);

        // SocialAuthService signs a returning user in by this exact (provider, provider user id) lookup; with no
        // row it falls back to e-mail matching, which task 4707 refuses for an address the provider did not verify.
        using var read = scene.NewContext();
        Assert.False(await read.ExternalLogins.AnyAsync(e => e.Provider == "Microsoft" && e.ProviderUserId == "attacker-ms-id"));
    }

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_RoleAlreadyHeld_IsNotGrantedTwice()
    {
        var scene = CreateScene();
        var attacker = SeedAttackerAccount(scene);
        using (var context = scene.NewContext())
        {
            context.UserRoles.Add(new UserRole { UserId = attacker.Id, RoleId = scene.Role.Id, TenantId = scene.Tenant.Id });
            context.SaveChanges();
        }

        Assert.True((await AcceptAsync(scene, NewPassword)).Success);

        using var read = scene.NewContext();
        Assert.Single(await read.UserRoles.Where(ur => ur.UserId == attacker.Id).ToListAsync());
    }

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_DoesNotTouchOtherUsersLoginsOrTokens()
    {
        var scene = CreateScene();
        SeedAttackerAccount(scene);
        Guid bystanderId;
        using (var context = scene.NewContext())
        {
            var bystander = new User { Email = "bystander@invited.test", PasswordHash = "x", EmailConfirmed = true, IsActive = true };
            context.Users.Add(bystander);
            context.ExternalLogins.Add(new ExternalLogin { UserId = bystander.Id, Provider = "Google", ProviderUserId = "bystander-google" });
            context.RefreshTokens.Add(new RefreshToken { UserId = bystander.Id, TokenHash = "bystander-refresh", ExpiresAt = DateTime.UtcNow.AddDays(7) });
            context.Set<OpenIddictEntityFrameworkCoreToken>().Add(new OpenIddictEntityFrameworkCoreToken
            {
                Id = Guid.NewGuid().ToString(), Subject = bystander.Id.ToString(), Status = "valid", Type = "refresh_token"
            });
            context.SaveChanges();
            bystanderId = bystander.Id;
        }

        Assert.True((await AcceptAsync(scene, NewPassword)).Success);

        using var read = scene.NewContext();
        Assert.Equal(1, await read.ExternalLogins.CountAsync(e => e.UserId == bystanderId));
        Assert.Equal(1, await read.RefreshTokens.CountAsync(t => t.UserId == bystanderId));
        Assert.Equal(1, await read.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == bystanderId.ToString() && t.Status == "valid"));
    }

    // ----- no new password: refused, and nothing is touched ------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Accept_UnconfirmedExistingAccount_WithoutNewPassword_IsRefused_AndLeavesTheAccountUntouched(string? password)
    {
        var scene = CreateScene();
        var attacker = SeedAttackerAccount(scene);

        var result = await AcceptAsync(scene, password);

        Assert.False(result.Success);
        Assert.Contains("new password", result.Error, StringComparison.OrdinalIgnoreCase);
        await AssertAccountUntouchedAsync(scene, attacker);
    }

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_CleanupAndRoleGrantAreOneSave()
    {
        var scene = CreateScene();
        SeedAttackerAccount(scene);

        using var tracking = new SaveTrackingContext(scene.Options);
        var result = await CreateService(tracking).AcceptInvitationAsync(scene.Invitation.Token, NewPassword, "Real", "Owner");

        Assert.True(result.Success);
        Assert.Equal(1, tracking.SaveCount); // cleanup, role grant, invitation status and audit entry in one commit
    }

    [Fact]
    public async Task Accept_UnconfirmedExistingAccount_WhenTheSaveFails_NothingChanges()
    {
        var scene = CreateScene();
        var attacker = SeedAttackerAccount(scene);

        using (var failing = new SaveTrackingContext(scene.Options) { FailOnSave = true })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateService(failing).AcceptInvitationAsync(scene.Invitation.Token, NewPassword, "Real", "Owner"));
        }

        await AssertAccountUntouchedAsync(scene, attacker);
    }

    [Fact]
    public async Task Accept_ForAPrivilegedRole_FromANonSuperAdminSender_IsRefused_AndLeavesTheUnconfirmedAccountUntouched()
    {
        var scene = CreateScene();
        var attacker = SeedAttackerAccount(scene);
        using (var context = scene.NewContext())
        {
            var systemAdmin = new Role { Id = Guid.NewGuid(), Name = "SystemAdmin", Permissions = "[\"*\"]" };
            context.Roles.Add(systemAdmin);
            var invitation = await context.Set<Invitation>().SingleAsync();
            invitation.RoleId = systemAdmin.Id;
            context.SaveChanges();
        }

        var result = await AcceptAsync(scene, NewPassword);

        Assert.False(result.Success);
        using var read = scene.NewContext();
        var user = await read.Users.AsNoTracking().SingleAsync(u => u.Id == attacker.Id);
        Assert.False(user.EmailConfirmed);
        Assert.Equal(attacker.PasswordHash, user.PasswordHash);
        Assert.Equal(1, await read.ExternalLogins.CountAsync(e => e.UserId == attacker.Id));
        Assert.Equal(1, await read.RefreshTokens.CountAsync(t => t.UserId == attacker.Id));
        Assert.Empty(await read.UserRoles.ToListAsync());
    }

    // ----- everything else behaves as before -----------------------------------------------------------------

    [Fact]
    public async Task Accept_ConfirmedExistingAccount_IsNotCleaned_AndPasswordIsIgnored()
    {
        var scene = CreateScene();
        var existing = SeedAttackerAccount(scene, emailConfirmed: true);

        var result = await AcceptAsync(scene, NewPassword);

        Assert.True(result.Success);
        using var read = scene.NewContext();
        var user = await read.Users.AsNoTracking().SingleAsync(u => u.Email == Email);
        Assert.Equal(existing.PasswordHash, user.PasswordHash); // password untouched
        Assert.Equal("Mallory", user.FirstName); // names untouched
        Assert.True(user.TwoFactorEnabled);
        Assert.Equal("+31600000000", user.PhoneNumber);
        Assert.Equal(1, await read.ExternalLogins.CountAsync());
        Assert.Equal(1, await read.Credentials.CountAsync());
        Assert.Equal(1, await read.RefreshTokens.CountAsync());
        Assert.Equal(1, await read.RecoveryCodes.CountAsync());
        Assert.True((await read.ApiKeys.SingleAsync()).IsActive);
        Assert.False((await read.UserSessions.SingleAsync()).IsRevoked);
        Assert.Equal("valid", (await read.Set<OpenIddictEntityFrameworkCoreToken>().SingleAsync()).Status);
        Assert.Single(await read.UserRoles.Where(ur => ur.UserId == existing.Id).ToListAsync());
        Assert.Empty(await read.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Accept_ConfirmedExistingAccount_WithoutPassword_StillWorks()
    {
        var scene = CreateScene();
        SeedAttackerAccount(scene, emailConfirmed: true);

        Assert.True((await AcceptAsync(scene, password: null)).Success);
    }

    [Fact]
    public async Task Accept_BrandNewAccount_IsCreatedConfirmed_AndStillNeedsAPassword()
    {
        var scene = CreateScene("brand-new@invited.test");

        var withoutPassword = await AcceptAsync(scene, password: null);
        Assert.False(withoutPassword.Success);
        Assert.Equal("Password is required for new accounts", withoutPassword.Error);

        var result = await AcceptAsync(scene, NewPassword);
        Assert.True(result.Success);
        using var read = scene.NewContext();
        var user = await read.Users.AsNoTracking().SingleAsync(u => u.Email == "brand-new@invited.test");
        Assert.True(user.EmailConfirmed);
        Assert.True(BCrypt.Net.BCrypt.Verify(NewPassword, user.PasswordHash));
        Assert.Single(await read.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync());
        Assert.Empty(await read.AuditLogs.ToListAsync()); // no account reset happened
    }

    /// <summary>Counts the saves that reach the database and can make them fail, standing in for a commit-time error.</summary>
    private sealed class SaveTrackingContext : IAMDbContext
    {
        public SaveTrackingContext(DbContextOptions<IAMDbContext> options) : base(options) { }

        public int SaveCount { get; private set; }
        public bool FailOnSave { get; init; }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (FailOnSave) throw new InvalidOperationException("simulated database failure");
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }
}
