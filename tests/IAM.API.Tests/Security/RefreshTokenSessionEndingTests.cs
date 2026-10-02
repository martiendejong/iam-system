using System.Security.Cryptography;
using System.Text;
using IAM.API.Controllers;
using IAM.Core;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 4709: a refresh token must stop working the moment its user can no longer sign in.
/// Covers (1) the refresh check on inactive accounts, (2) every route that deactivates or locks
/// a user revoking that user's refresh tokens through the one shared helper, (3) a forced
/// password change revoking them, and (4) replay of an already-rotated token ending all of the
/// user's sessions - without that detector firing for tokens revoked for other reasons.
/// </summary>
public class RefreshTokenSessionEndingTests
{
    private const string Password = "Password123!";
    private const string InvalidTokenError = "Invalid or expired refresh token";

    private static IAMDbContext CreateContext(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            // Account merge and erasure open a relational transaction; the in-memory store has none.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new IAMDbContext(options);
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5"
        })
        .Build();

    private static AuthService CreateAuthService(IAMDbContext context, ILogger<AuthService>? logger = null)
    {
        var emailService = new FakeEmailService();
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://iam.example.com" });
        var otpService = new OtpService(context, emailService, new FakeSmsService(), emailSettings, NullLogger<OtpService>.Instance);
        var riskAssessmentService = new RiskAssessmentService(context, NullLogger<RiskAssessmentService>.Instance);
        return new AuthService(
            context, CreateConfiguration(), emailService, riskAssessmentService, otpService,
            new ClaimsMappingService(context), logger ?? NullLogger<AuthService>.Instance);
    }

    private static async Task<User> AddUserAsync(IAMDbContext context, string name, bool isActive = true, string? passwordHash = null)
    {
        var user = new User
        {
            Email = $"{name}@example.com",
            PasswordHash = passwordHash ?? BCrypt.Net.BCrypt.HashPassword(Password),
            FirstName = name,
            LastName = "Tester",
            EmailConfirmed = true,
            IsActive = isActive
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<string> LoginAsync(AuthService auth, User user, bool rememberMe = false)
    {
        var result = await auth.LoginAsync(user.Email, Password, rememberMe: rememberMe);
        Assert.True(result.Success, result.Error);
        return result.RefreshToken!;
    }

    /// <summary>Adds an unrevoked refresh token row directly (for routes that never log in).</summary>
    private static async Task<RefreshToken> AddTokenAsync(IAMDbContext context, Guid userId)
    {
        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        context.RefreshTokens.Add(token);
        await context.SaveChangesAsync();
        return token;
    }

    private static async Task<int> ActiveTokenCountAsync(IAMDbContext context, Guid userId) =>
        await context.RefreshTokens.AsNoTracking().CountAsync(rt => rt.UserId == userId && rt.RevokedAt == null);

    // ───────────────────────── 1. refresh check on inactive accounts ─────────────────────────

    [Fact]
    public async Task Refresh_ActiveUser_RotatesTokenAndIssuesNewPair()
    {
        var context = CreateContext();
        var auth = CreateAuthService(context);
        var user = await AddUserAsync(context, "active");
        var oldToken = await LoginAsync(auth, user);

        var result = await auth.RefreshTokenAsync(oldToken);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        Assert.NotEqual(oldToken, result.RefreshToken);
        Assert.Equal(1, await ActiveTokenCountAsync(context, user.Id)); // old spent, new live
        Assert.False((await auth.RefreshTokenAsync(oldToken)).Success); // single use still holds
    }

    [Fact]
    public async Task Refresh_ValidTokenOfInactiveUser_ReturnsGenericInvalidTokenErrorAndIssuesNothing()
    {
        var context = CreateContext();
        var auth = CreateAuthService(context);
        var user = await AddUserAsync(context, "offboarded");
        var token = await LoginAsync(auth, user);

        user.IsActive = false; // deactivated by a route that did not revoke (e.g. a direct DB edit)
        await context.SaveChangesAsync();

        var result = await auth.RefreshTokenAsync(token);
        var unknownTokenResult = await auth.RefreshTokenAsync("not-a-real-token");

        Assert.False(result.Success);
        Assert.Equal(InvalidTokenError, result.Error);
        Assert.Equal(unknownTokenResult.Error, result.Error); // no account-state oracle
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.User);
        Assert.Equal(1, await context.RefreshTokens.CountAsync(rt => rt.UserId == user.Id)); // nothing issued
    }

    [Fact]
    public async Task Refresh_AfterUserIsReactivated_WorksAgainWithTheSameToken()
    {
        var context = CreateContext();
        var auth = CreateAuthService(context);
        var user = await AddUserAsync(context, "reactivated");
        var token = await LoginAsync(auth, user);

        user.IsActive = false;
        await context.SaveChangesAsync();
        Assert.False((await auth.RefreshTokenAsync(token)).Success);

        user.IsActive = true;
        await context.SaveChangesAsync();
        var result = await auth.RefreshTokenAsync(token);

        Assert.True(result.Success); // a refused attempt must not burn the token
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
    }

    [Fact]
    public async Task Refresh_RememberMeSession_KeepsItsThirtyDayFloorAcrossRotation()
    {
        var context = CreateContext();
        var auth = CreateAuthService(context);
        var user = await AddUserAsync(context, "remembered");
        var token = await LoginAsync(auth, user, rememberMe: true);

        var result = await auth.RefreshTokenAsync(token);

        Assert.True(result.Success);
        Assert.True(result.RefreshTokenLifetimeDays >= AuthConstants.RememberMeMinimumDays);
        var successor = await context.RefreshTokens.AsNoTracking()
            .Where(rt => rt.UserId == user.Id && rt.RevokedAt == null).SingleAsync();
        Assert.True(successor.RememberMe);
        Assert.True(successor.ExpiresAt > DateTime.UtcNow.AddDays(AuthConstants.RememberMeMinimumDays - 1));
    }

    // ───────────────────────── 2. every deactivate / lock route revokes ─────────────────────────

    [Fact]
    public async Task RevokeRefreshTokens_RevokesOnlyTheTargetUsersLiveTokens_AndKeepsEarlierRevocationTimes()
    {
        var context = CreateContext();
        var target = await AddUserAsync(context, "target");
        var bystander = await AddUserAsync(context, "bystander");
        var live1 = await AddTokenAsync(context, target.Id);
        var live2 = await AddTokenAsync(context, target.Id);
        var earlier = await AddTokenAsync(context, target.Id);
        var otherUsersToken = await AddTokenAsync(context, bystander.Id);
        var earlierRevokedAt = DateTime.UtcNow.AddDays(-2);
        earlier.RevokedAt = earlierRevokedAt;
        await context.SaveChangesAsync();

        var revoked = await context.RevokeRefreshTokensAsync(target.Id);
        await context.SaveChangesAsync();

        Assert.Equal(2, revoked);
        Assert.NotNull((await context.RefreshTokens.FindAsync(live1.Id))!.RevokedAt);
        Assert.NotNull((await context.RefreshTokens.FindAsync(live2.Id))!.RevokedAt);
        Assert.Equal(earlierRevokedAt, (await context.RefreshTokens.FindAsync(earlier.Id))!.RevokedAt);
        Assert.Null((await context.RefreshTokens.FindAsync(otherUsersToken.Id))!.RevokedAt);
        Assert.Equal(0, await context.RevokeRefreshTokensAsync(Array.Empty<Guid>()));
    }

    [Fact]
    public async Task Scim_DeleteUser_RevokesRefreshTokens()
    {
        var context = CreateContext();
        var scim = new ScimService(context);
        var user = await AddUserAsync(context, "scim-delete");
        var other = await AddUserAsync(context, "scim-other");
        await AddTokenAsync(context, user.Id);
        await AddTokenAsync(context, other.Id);

        Assert.True(await scim.DeleteUserAsync(Guid.NewGuid(), user.Id));

        Assert.False((await context.Users.FindAsync(user.Id))!.IsActive);
        Assert.Equal(0, await ActiveTokenCountAsync(context, user.Id));
        Assert.Equal(1, await ActiveTokenCountAsync(context, other.Id));
    }

    [Theory]
    [InlineData("False")]
    [InlineData(false)]
    public async Task Scim_PatchActiveFalse_RevokesRefreshTokens(object value)
    {
        var context = CreateContext();
        var scim = new ScimService(context);
        var user = await AddUserAsync(context, "scim-patch");
        await AddTokenAsync(context, user.Id);

        await scim.PatchUserAsync(Guid.NewGuid(), user.Id, ActivePatch(value));

        Assert.False((await context.Users.FindAsync(user.Id))!.IsActive);
        Assert.Equal(0, await ActiveTokenCountAsync(context, user.Id));
    }

    [Fact]
    public async Task Scim_PatchThatKeepsUserActive_LeavesRefreshTokensAlone()
    {
        var context = CreateContext();
        var scim = new ScimService(context);
        var user = await AddUserAsync(context, "scim-keep");
        await AddTokenAsync(context, user.Id);

        await scim.PatchUserAsync(Guid.NewGuid(), user.Id, ActivePatch("True"));
        await scim.PatchUserAsync(Guid.NewGuid(), user.Id, new ScimPatchRequest
        {
            Operations = { new ScimPatchOperation { Op = "replace", Path = "name.givenName", Value = "Renamed" } }
        });

        Assert.True((await context.Users.FindAsync(user.Id))!.IsActive);
        Assert.Equal(1, await ActiveTokenCountAsync(context, user.Id));
    }

    [Fact]
    public async Task Scim_ReplaceUserWithActiveFalse_RevokesRefreshTokens_AndActiveTrueDoesNot()
    {
        var context = CreateContext();
        var scim = new ScimService(context);
        var stays = await AddUserAsync(context, "scim-put-stays");
        var leaves = await AddUserAsync(context, "scim-put-leaves");
        await AddTokenAsync(context, stays.Id);
        await AddTokenAsync(context, leaves.Id);

        await scim.ReplaceUserAsync(Guid.NewGuid(), stays.Id, new ScimUserResource { UserName = stays.Email, Active = true });
        await scim.ReplaceUserAsync(Guid.NewGuid(), leaves.Id, new ScimUserResource { UserName = leaves.Email, Active = false });

        Assert.Equal(1, await ActiveTokenCountAsync(context, stays.Id));
        Assert.False((await context.Users.FindAsync(leaves.Id))!.IsActive);
        Assert.Equal(0, await ActiveTokenCountAsync(context, leaves.Id));
    }

    [Fact]
    public async Task DirectorySync_DisablingMissingUsers_RevokesOnlyTheirRefreshTokens()
    {
        var context = CreateContext();
        var sync = new DirectorySyncService(context, NullLogger<DirectorySyncService>.Instance);
        var removed = await AddUserAsync(context, "ldap-removed", passwordHash: "LDAP_MANAGED");
        var stillListed = await AddUserAsync(context, "ldap-listed", passwordHash: "LDAP_MANAGED");
        var local = await AddUserAsync(context, "local-user"); // not directory-managed
        await AddTokenAsync(context, removed.Id);
        await AddTokenAsync(context, removed.Id);
        await AddTokenAsync(context, stillListed.Id);
        await AddTokenAsync(context, local.Id);

        var disabled = await sync.DisableMissingUsersAsync(
            Guid.NewGuid(), new HashSet<string> { stillListed.Email }, CancellationToken.None);

        Assert.Equal(1, disabled);
        Assert.False((await context.Users.FindAsync(removed.Id))!.IsActive);
        Assert.Equal(0, await ActiveTokenCountAsync(context, removed.Id));
        Assert.Equal(1, await ActiveTokenCountAsync(context, stillListed.Id));
        Assert.Equal(1, await ActiveTokenCountAsync(context, local.Id));
    }

    [Fact]
    public async Task SecurityAlert_LockAccountAutoResponse_DeactivatesUserAndRevokesRefreshTokens()
    {
        var context = CreateContext();
        var alerts = CreateSecurityAlertService(context);
        var user = await AddUserAsync(context, "locked-by-alert");
        var bystander = await AddUserAsync(context, "not-alerted");
        await AddTokenAsync(context, user.Id);
        await AddTokenAsync(context, bystander.Id);
        var rule = await AddAlertRuleAsync(context, "lock_account");

        await alerts.FireAlertAsync(rule.Id, "Brute force", $"{{\"userId\":\"{user.Id}\"}}");

        Assert.False((await context.Users.FindAsync(user.Id))!.IsActive);
        Assert.Equal(0, await ActiveTokenCountAsync(context, user.Id));
        Assert.Equal(1, await ActiveTokenCountAsync(context, bystander.Id));
    }

    [Fact]
    public async Task SecurityAlert_ForceMfaAutoResponse_DoesNotRevokeRefreshTokens()
    {
        var context = CreateContext();
        var alerts = CreateSecurityAlertService(context);
        var user = await AddUserAsync(context, "mfa-forced");
        await AddTokenAsync(context, user.Id);
        var rule = await AddAlertRuleAsync(context, "force_mfa");

        await alerts.FireAlertAsync(rule.Id, "Suspicious", $"{{\"userId\":\"{user.Id}\"}}");

        Assert.True((await context.Users.FindAsync(user.Id))!.IsActive);
        Assert.Equal(1, await ActiveTokenCountAsync(context, user.Id));
    }

    [Fact]
    public async Task AccountMerge_RevokesTheDeactivatedSecondaryAccountsRefreshTokens()
    {
        var context = CreateContext();
        var linking = new AccountLinkingService(context, NullLogger<AccountLinkingService>.Instance);
        var primary = await AddUserAsync(context, "merge-primary");
        var secondary = await AddUserAsync(context, "merge-secondary");
        await AddTokenAsync(context, primary.Id);
        await AddTokenAsync(context, secondary.Id);

        var result = await linking.MergeAccountsAsync(primary.Id, secondary.Id);

        Assert.True(result.Success, result.Error);
        Assert.False((await context.Users.FindAsync(secondary.Id))!.IsActive);
        Assert.Equal(0, await ActiveTokenCountAsync(context, secondary.Id));
        Assert.Equal(1, await ActiveTokenCountAsync(context, primary.Id));
    }

    [Fact]
    public async Task DataErasure_RevokesRefreshTokens()
    {
        var context = CreateContext();
        var requests = new DataRequestService(context, new FakeEmailService(), NullLogger<DataRequestService>.Instance);
        var user = await AddUserAsync(context, "erased");
        await AddTokenAsync(context, user.Id);
        var request = await requests.CreateDeletionRequestAsync(user.Id, "test");

        await requests.ProcessDeletionAsync(request.Id, Guid.NewGuid());

        Assert.False((await context.Users.FindAsync(user.Id))!.IsActive);
        Assert.Equal(0, await ActiveTokenCountAsync(context, user.Id));
    }

    [Fact]
    public async Task AdminDeactivate_RevokesRefreshTokens()
    {
        var context = CreateContext();
        var controller = new UsersController(context, CreateAuthService(context));
        var user = await AddUserAsync(context, "admin-deactivated");
        await AddTokenAsync(context, user.Id);

        var response = await controller.DeactivateUser(user.Id);

        Assert.IsType<OkObjectResult>(response);
        Assert.False((await context.Users.FindAsync(user.Id))!.IsActive);
        Assert.Equal(0, await ActiveTokenCountAsync(context, user.Id));
    }

    // ───────────────────────── 3. forced password change revokes ─────────────────────────

    [Fact]
    public async Task AdminPasswordChange_RevokesTheUsersRefreshTokens_AndOnlyTheirs()
    {
        var context = CreateContext();
        var auth = CreateAuthService(context);
        var controller = new UsersController(context, auth);
        var user = await AddUserAsync(context, "pw-changed");
        var other = await AddUserAsync(context, "pw-bystander");
        var sessionToken = await LoginAsync(auth, user);
        await AddTokenAsync(context, other.Id);

        var response = await controller.ChangeUserPassword(user.Id, new AdminChangePasswordRequest("BrandNewPassw0rd!"));

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(0, await ActiveTokenCountAsync(context, user.Id));
        Assert.Equal(1, await ActiveTokenCountAsync(context, other.Id));
        Assert.False((await auth.RefreshTokenAsync(sessionToken)).Success);
        Assert.True(BCrypt.Net.BCrypt.Verify("BrandNewPassw0rd!", (await context.Users.FindAsync(user.Id))!.PasswordHash));
    }

    [Fact]
    public async Task AdminPasswordChange_RejectedForShortPassword_LeavesSessionsAlone()
    {
        var context = CreateContext();
        var controller = new UsersController(context, CreateAuthService(context));
        var user = await AddUserAsync(context, "pw-rejected");
        await AddTokenAsync(context, user.Id);

        var response = await controller.ChangeUserPassword(user.Id, new AdminChangePasswordRequest("short"));

        Assert.IsType<BadRequestObjectResult>(response);
        Assert.Equal(1, await ActiveTokenCountAsync(context, user.Id));
    }

    [Fact]
    public async Task PasswordReset_StillRevokesRefreshTokens_AndStaleTokenDoesNotKillTheNewSession()
    {
        var context = CreateContext();
        var auth = CreateAuthService(context);
        var user = await AddUserAsync(context, "reset");
        var staleToken = await LoginAsync(auth, user); // e.g. a browser on another device

        const string rawResetToken = "reset-token-4709";
        user.PasswordResetToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawResetToken)));
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
        await context.SaveChangesAsync();
        Assert.True(await auth.ResetPasswordAsync(rawResetToken, "AnotherPassw0rd!"));
        Assert.Equal(0, await ActiveTokenCountAsync(context, user.Id));

        var newSession = (await auth.LoginAsync(user.Email, "AnotherPassw0rd!")).RefreshToken!;

        // The old device now auto-refreshes with its cookie. That is a stale client, not theft:
        // it must be refused without ending the session the user just opened.
        Assert.False((await auth.RefreshTokenAsync(staleToken)).Success);
        Assert.Equal(1, await ActiveTokenCountAsync(context, user.Id));
        Assert.True((await auth.RefreshTokenAsync(newSession)).Success);
    }

    // ───────────────────────── 4. replay of a rotated token ─────────────────────────

    [Fact]
    public async Task Replay_OfRotatedToken_RevokesAllOfTheUsersActiveTokens_AndIsLogged()
    {
        var context = CreateContext();
        var log = new CapturingLogger<AuthService>();
        var auth = CreateAuthService(context, log);
        var user = await AddUserAsync(context, "replayed");
        var bystander = await AddUserAsync(context, "replay-bystander");
        var phoneToken = await LoginAsync(auth, user);
        var laptopToken = await LoginAsync(auth, user);
        var bystanderToken = await LoginAsync(auth, bystander);

        var rotated = await auth.RefreshTokenAsync(phoneToken); // legitimate rotation
        Assert.True(rotated.Success);
        Assert.Equal(2, await ActiveTokenCountAsync(context, user.Id)); // laptop + rotated phone

        var replay = await auth.RefreshTokenAsync(phoneToken, "203.0.113.9", "thief/1.0"); // the spent token again

        Assert.False(replay.Success);
        Assert.Equal(InvalidTokenError, replay.Error);
        Assert.Null(replay.AccessToken);
        Assert.Null(replay.RefreshToken);
        Assert.Equal(0, await ActiveTokenCountAsync(context, user.Id));
        Assert.False((await auth.RefreshTokenAsync(rotated.RefreshToken!)).Success);
        Assert.False((await auth.RefreshTokenAsync(laptopToken)).Success);
        Assert.Equal(1, await ActiveTokenCountAsync(context, bystander.Id));
        Assert.True((await auth.RefreshTokenAsync(bystanderToken)).Success);

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("replayed", warning.Message);
        Assert.Contains(user.Id.ToString(), warning.Message);
        Assert.Contains("203.0.113.9", warning.Message);
        Assert.DoesNotContain(phoneToken, warning.Message);

        var audit = Assert.Single(await context.AuditLogs.Where(a => a.Action == "RefreshTokenReplayDetected").ToListAsync());
        Assert.Equal(user.Id, audit.UserId);
        Assert.Equal("203.0.113.9", audit.IpAddress);
        Assert.Equal("thief/1.0", audit.UserAgent);
        Assert.DoesNotContain(phoneToken, audit.Details);
    }

    [Fact]
    public async Task Replay_IsDetectedFromAFreshDatabaseContext()
    {
        // Rotation is recorded as "successor created at the exact instant its predecessor was
        // revoked"; prove that survives a write + re-read instead of living in the change tracker.
        var databaseName = Guid.NewGuid().ToString();
        string spentToken;
        Guid userId;
        await using (var context = CreateContext(databaseName))
        {
            var auth = CreateAuthService(context);
            var user = await AddUserAsync(context, "fresh-context");
            userId = user.Id;
            spentToken = await LoginAsync(auth, user);
            Assert.True((await auth.RefreshTokenAsync(spentToken)).Success);
        }

        await using (var readBack = CreateContext(databaseName))
        {
            var tokens = await readBack.RefreshTokens.AsNoTracking().Where(rt => rt.UserId == userId).ToListAsync();
            var spent = Assert.Single(tokens, t => t.RevokedAt != null);
            var successor = Assert.Single(tokens, t => t.RevokedAt == null);
            Assert.Equal(spent.RevokedAt, successor.CreatedAt);
            Assert.True(await readBack.IsRotatedAsync(spent));
            Assert.False(await readBack.IsRotatedAsync(successor));
        }

        await using (var replayContext = CreateContext(databaseName))
        {
            Assert.False((await CreateAuthService(replayContext).RefreshTokenAsync(spentToken)).Success);
            Assert.Equal(0, await ActiveTokenCountAsync(replayContext, userId));
        }
    }

    [Fact]
    public async Task Replay_OfLoggedOutToken_IsRefusedWithoutEndingOtherSessions()
    {
        var context = CreateContext();
        var log = new CapturingLogger<AuthService>();
        var auth = CreateAuthService(context, log);
        var user = await AddUserAsync(context, "logged-out");
        var loggedOut = await LoginAsync(auth, user);
        var otherDevice = await LoginAsync(auth, user);
        Assert.True(await auth.RevokeTokenAsync(loggedOut));

        var result = await auth.RefreshTokenAsync(loggedOut);

        Assert.False(result.Success);
        Assert.Equal(1, await ActiveTokenCountAsync(context, user.Id));
        Assert.True((await auth.RefreshTokenAsync(otherDevice)).Success);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Empty(await context.AuditLogs.Where(a => a.Action == "RefreshTokenReplayDetected").ToListAsync());
    }

    [Fact]
    public async Task Replay_AfterTheUserWasDeactivated_StaysGenericAndRevokesNothingMore()
    {
        var context = CreateContext();
        var auth = CreateAuthService(context);
        var controller = new UsersController(context, auth);
        var user = await AddUserAsync(context, "deactivated-then-replayed");
        var token = await LoginAsync(auth, user);
        await controller.DeactivateUser(user.Id);

        var result = await auth.RefreshTokenAsync(token);

        Assert.False(result.Success);
        Assert.Equal(InvalidTokenError, result.Error);
        Assert.Empty(await context.AuditLogs.Where(a => a.Action == "RefreshTokenReplayDetected").ToListAsync());
    }

    // ───────────────────────── helpers ─────────────────────────

    private static ScimPatchRequest ActivePatch(object value) => new()
    {
        Operations = { new ScimPatchOperation { Op = "replace", Path = "active", Value = value } }
    };

    private static SecurityAlertService CreateSecurityAlertService(IAMDbContext context) =>
        new(context, new FakeEmailService(), new NoHttpClientFactory(), NullLogger<SecurityAlertService>.Instance);

    private static async Task<AlertRule> AddAlertRuleAsync(IAMDbContext context, string autoResponse)
    {
        var rule = new AlertRule { Name = $"rule-{autoResponse}", AutoResponseAction = autoResponse, Channels = "[]" };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();
        return rule;
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No outbound HTTP expected in this test");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private class FakeEmailService : IEmailService
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

    private class FakeSmsService : ISmsService
    {
        public Task<bool> SendSmsAsync(string phoneNumber, string message) => Task.FromResult(true);
    }
}
