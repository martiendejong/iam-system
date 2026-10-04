using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 3162. TOTP moved from HMAC-SHA1 to HMAC-SHA256 (PR #104), so an authenticator app enrolled before that
/// generates codes the server can never accept again (<see cref="User.HasLegacyTotpEnrollment"/>).
/// <para>
/// Decision (Martien, 2026-10-04): handle it through an e-mail PIN, and let each tenant configure the behaviour
/// (<see cref="OrganizationSettings.LegacyTotpMigration"/>). When a legacy member signs in and has passed the first
/// proof (password, magic link or OTP), the account moves to the e-mail-PIN second factor that already exists
/// (<see cref="TwoFactorMethod.Email"/>): the caller then falls into the normal "send a PIN, require it before any
/// token is issued" branch, so the member is never left without a working second factor and never locked out.
/// </para>
/// <para>
/// Nothing here sends mail in bulk: the PIN is mailed by the existing login-2FA flow, and only when the member
/// signs in. The member can set up an authenticator app again afterwards (new enrollments are SHA-256).
/// </para>
/// </summary>
public static class LegacyTotpMigration
{
    public const string NoticeMessage =
        "Your authenticator app was set up before a security upgrade and no longer works. " +
        "From now on you confirm your sign-in with a PIN we send to your e-mail address.";

    /// <summary>
    /// The mode that applies to a user. A user can hold roles in several tenants: leaving the legacy enrollment
    /// untouched needs every one of them to opt out, otherwise the user is migrated (the protective choice).
    /// Users without a tenant, and tenants without a settings row, get the default (<see cref="LegacyTotpMigrationMode.EmailPin"/>).
    /// </summary>
    public static async Task<LegacyTotpMigrationMode> ResolveModeAsync(
        IAMDbContext context, Guid userId, CancellationToken ct = default)
    {
        var tenantIds = await context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId && ur.TenantId != null)
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (tenantIds.Count == 0)
            return LegacyTotpMigrationMode.EmailPin;

        // OrganizationSettings is unique per tenant, so the number of "off" rows is the number of opted-out tenants.
        var optedOut = await context.OrganizationSettings
            .AsNoTracking()
            .CountAsync(os => tenantIds.Contains(os.TenantId) && os.LegacyTotpMigration == LegacyTotpMigrationMode.Off, ct);

        return optedOut == tenantIds.Count ? LegacyTotpMigrationMode.Off : LegacyTotpMigrationMode.EmailPin;
    }

    /// <summary>
    /// Moves a legacy authenticator-app user to e-mail-PIN two-factor and returns the notice to show them, or returns
    /// null when nothing was changed (not a legacy enrollment, e-mail not confirmed so a PIN could not be trusted,
    /// inactive account, or the tenant policy is off). The caller must have verified the user's first proof already.
    /// </summary>
    public static async Task<LegacyTotpMigrationNotice?> TryMigrateToEmailPinAsync(
        IAMDbContext context,
        User user,
        ILogger logger,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken ct = default)
    {
        if (!user.HasLegacyTotpEnrollment() || !user.EmailConfirmed || !user.IsActive)
            return null;

        if (await ResolveModeAsync(context, user.Id, ct) == LegacyTotpMigrationMode.Off)
            return null;

        // The recovery codes belong to the authenticator enrollment and cannot be used with e-mail two-factor.
        var recoveryCodes = await context.RecoveryCodes
            .Where(rc => rc.UserId == user.Id)
            .ToListAsync(ct);
        context.RecoveryCodes.RemoveRange(recoveryCodes);

        user.TwoFactorMethod = TwoFactorMethod.Email;
        user.TwoFactorSecret = null;
        user.TotpAlgorithm = null;
        user.UpdatedAt = DateTime.UtcNow;

        context.AuditLogs.Add(new AuditLog
        {
            UserId = user.Id,
            Action = "LegacyTotpMigratedToEmailPin",
            Resource = "User",
            Details = JsonSerializer.Serialize(new { from = "totp", to = "email", removedRecoveryCodes = recoveryCodes.Count }),
            IpAddress = ipAddress,
            UserAgent = userAgent
        });

        await context.SaveChangesAsync(ct);

        logger.LogInformation(
            "Legacy SHA-1 authenticator enrollment moved to e-mail PIN two-factor. UserId: {UserId}", user.Id);

        return new LegacyTotpMigrationNotice("totp", "email", NoticeMessage);
    }
}
