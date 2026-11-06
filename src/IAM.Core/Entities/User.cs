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

    /// <summary>
    /// Task 5490: per-user stamp carried in the IAM.Session single sign-on cookie and checked on every
    /// /connect/authorize. Rotating it (logout, password reset or change, deactivation, forced revoke)
    /// makes every older cookie worthless. Global per user: all of the user's browsers sign in again.
    /// </summary>
    public string SecurityStamp { get; set; } = NewSecurityStamp();

    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

    /// <summary>Ends every existing single sign-on cookie of this user.</summary>
    public void RotateSecurityStamp() => SecurityStamp = NewSecurityStamp();

    /// <summary>Rows that pre-date the column hold an empty stamp; give them a real one at next sign-in.</summary>
    public void EnsureSecurityStamp()
    {
        if (string.IsNullOrEmpty(SecurityStamp)) SecurityStamp = NewSecurityStamp();
    }

    // MFA
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    public TwoFactorMethod TwoFactorMethod { get; set; } = TwoFactorMethod.None;

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
}

public enum TwoFactorMethod
{
    None,
    Totp,
    Email
}
