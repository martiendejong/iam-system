using System.Linq.Expressions;

namespace IAM.Core.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? AvatarUrl { get; set; }

    // Email verification
    public bool EmailConfirmed { get; set; }
    public string? EmailVerificationToken { get; set; }
    public DateTime? EmailVerificationTokenExpiry { get; set; }

    // Security
    public bool IsActive { get; set; } = true;
    public bool IsLockedOut { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public int FailedLoginAttempts { get; set; }

    // MFA
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    public TwoFactorMethod TwoFactorMethod { get; set; } = TwoFactorMethod.None;

    /// <summary>
    /// HMAC algorithm the stored authenticator-app secret was provisioned for (see <see cref="TotpAlgorithms"/>).
    /// Set when a TOTP enrollment is activated. A TOTP user without it enrolled before the SHA-1 to SHA-256
    /// upgrade (task 3162) and is a legacy enrollment, see <see cref="HasLegacyTotpEnrollment"/>.
    /// </summary>
    public string? TotpAlgorithm { get; set; }

    // Password reset
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiry { get; set; }

    // Org hierarchy + principal classification (task 4057)
    /// <summary>
    /// Optional manager (another user). FK with ON DELETE SET NULL.
    /// No org data is seeded; admins set this via PUT /api/users/{id}/principal.
    /// </summary>
    public Guid? ManagerUserId { get; set; }
    public User? ManagerUser { get; set; }

    /// <summary>Human / Agent / Service. Users default to Human.</summary>
    public PrincipalKind PrincipalKind { get; set; } = PrincipalKind.Human;

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    // Navigation properties
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
    public ICollection<RecoveryCode> RecoveryCodes { get; set; } = new List<RecoveryCode>();

    /// <summary>
    /// True when this user has an active authenticator-app (TOTP) enrollment that was not provisioned for
    /// HMAC-SHA256. Such an app generates SHA-1 codes the server no longer accepts, so the enrollment can
    /// never pass a check again and has to be migrated (task 3162).
    /// </summary>
    public bool HasLegacyTotpEnrollment() => LegacyTotpEnrollmentCompiled(this);

    /// <summary>
    /// Query form of <see cref="HasLegacyTotpEnrollment"/> (same predicate, translated to SQL) so a count of
    /// affected members can never drift from the rule used at sign-in.
    /// </summary>
    public static readonly Expression<Func<User, bool>> LegacyTotpEnrollment = u =>
        u.TwoFactorEnabled
        && u.TwoFactorMethod == TwoFactorMethod.Totp
        && u.TotpAlgorithm != TotpAlgorithms.Sha256;

    private static readonly Func<User, bool> LegacyTotpEnrollmentCompiled = LegacyTotpEnrollment.Compile();
}

public enum TwoFactorMethod
{
    None,
    Totp,
    Email
}

public static class TotpAlgorithms
{
    /// <summary>Value stored in <see cref="User.TotpAlgorithm"/> for enrollments made after the SHA-256 upgrade.</summary>
    public const string Sha256 = "SHA256";
}
