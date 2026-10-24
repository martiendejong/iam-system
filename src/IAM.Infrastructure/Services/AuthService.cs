using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.Core;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Infrastructure.Services;

public class AuthService : IAuthService
{
    // Today's default when an organization has never saved a Token Configuration.
    private const int DefaultRefreshTokenLifetimeDays = 7;

    // Task 5166: one message for every way a password sign-in can fail before the password is proven.
    internal const string InvalidCredentialsMessage = "Invalid email or password";
    internal const string TooManyLoginAttemptsMessage = "Too many failed sign-in attempts. Please wait a moment and try again.";
    // The 2FA lock (task 4523); deliberately has no timestamp.
    internal const string AccountLockedMessage = "Account is temporarily locked. Try again later.";

    // Same cost as every stored hash (workFactor 12 is used everywhere passwords are set). Built once
    // per process; a random password nobody can type, so it never matches.
    private static readonly string DummyPasswordHash =
        BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), workFactor: 12);

    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly IRiskAssessmentService _riskAssessmentService;
    private readonly IOtpService _otpService;
    private readonly IClaimsMappingService _claimsMappingService;
    private readonly ILogger<AuthService> _logger;
    private readonly ILoginThrottle _loginThrottle;

    public AuthService(
        IAMDbContext context,
        IConfiguration configuration,
        IEmailService emailService,
        IRiskAssessmentService riskAssessmentService,
        IOtpService otpService,
        IClaimsMappingService claimsMappingService,
        ILogger<AuthService> logger,
        ILoginThrottle loginThrottle)
    {
        _context = context;
        _configuration = configuration;
        _emailService = emailService;
        _riskAssessmentService = riskAssessmentService;
        _otpService = otpService;
        _claimsMappingService = claimsMappingService;
        _logger = logger;
        _loginThrottle = loginThrottle;
    }

    /// <summary>
    /// One BCrypt verification whatever the input: against the stored hash when there is a usable one,
    /// otherwise against a dummy hash of the same cost (unknown email, or a stored value that is not a
    /// BCrypt hash, which would otherwise throw instantly and show up as a 500).
    /// </summary>
    private static bool VerifyPasswordWithUniformCost(string? password, string? storedHash)
    {
        var candidate = password ?? string.Empty;

        if (!string.IsNullOrEmpty(storedHash))
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(candidate, storedHash);
            }
            catch (BCrypt.Net.SaltParseException)
            {
                // Not a BCrypt hash: fall through to the dummy check so the cost is still paid.
            }
        }

        BCrypt.Net.BCrypt.Verify(candidate, DummyPasswordHash);
        return false;
    }

    /// <summary>
    /// Resolves the access/refresh token lifetime for a login, from the user's
    /// organization Token Configuration when one exists, otherwise today's defaults
    /// (Jwt:AccessTokenExpirationMinutes config, hardcoded 7-day refresh).
    /// </summary>
    private async Task<(int AccessTokenLifetimeMinutes, int RefreshTokenLifetimeDays)> ResolveTokenLifetimeAsync(Guid userId)
    {
        var defaultAccessMinutes = int.Parse(_configuration["Jwt:AccessTokenExpirationMinutes"] ?? "5");
        var orgLifetime = await _claimsMappingService.ResolveTokenLifetimeForUserAsync(userId);

        return orgLifetime != null
            ? (orgLifetime.AccessTokenLifetimeMinutes, orgLifetime.RefreshTokenLifetimeDays)
            : (defaultAccessMinutes, DefaultRefreshTokenLifetimeDays);
    }

    public async Task<AuthResult> RegisterAsync(string email, string password, string firstName, string lastName)
    {
        // Validate
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return new AuthResult
            {
                Success = false,
                Error = "Email and password are required"
            };
        }

        if (password.Length < 8)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Password must be at least 8 characters"
            };
        }

        // Check if user exists
        if (await _context.Users.AnyAsync(u => u.Email == email))
        {
            return new AuthResult
            {
                Success = false,
                Error = "Email already registered"
            };
        }

        // Create user — store the SHA-256 hash of the token; send the raw token in the email.
        var rawEmailToken = GenerateToken();
        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            FirstName = firstName,
            LastName = lastName,
            EmailConfirmed = false,
            EmailVerificationToken = HashVerificationToken(rawEmailToken),
            EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24),
            IsActive = true
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        await _emailService.SendEmailVerificationAsync(
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            rawEmailToken
        );

        return new AuthResult
        {
            Success = true,
            User = user
        };
    }

    public async Task<AuthResult> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null, string? returnUrl = null, bool rememberMe = false)
    {
        // Task 5166: repeated failures slow down THIS caller (typed email + IP) instead of locking the
        // real account, and the refusal is the same whether or not the account exists. It comes before
        // any lookup or BCrypt work, so a blocked caller costs next to nothing.
        var attempt = _loginThrottle.BeginAttempt(email, ipAddress);
        if (!attempt.Allowed)
        {
            return new AuthResult
            {
                Success = false,
                Error = TooManyLoginAttemptsMessage,
                RetryAfterSeconds = attempt.RetryAfterSeconds
            };
        }

        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == email);

        // Every attempt pays for exactly one BCrypt check, against a dummy hash of the same cost when
        // there is no (usable) account, so response time does not tell an unknown email from a real
        // one. This runs BEFORE the lock check: a wrong password on a locked account must look just
        // like any other wrong password.
        var passwordValid = VerifyPasswordWithUniformCost(password, user?.PasswordHash);

        if (user == null || !passwordValid)
        {
            return new AuthResult
            {
                Success = false,
                Error = InvalidCredentialsMessage
            };
        }

        _loginThrottle.RecordSuccess(email, ipAddress);

        // The persistent lock now only comes from the 2FA step (task 4523). It is reported only to a
        // caller who has proven the password, and without a timestamp.
        if (LoginLockout.IsLocked(user))
        {
            return new AuthResult
            {
                Success = false,
                Error = AccountLockedMessage
            };
        }

        // Password failures no longer touch FailedLoginAttempts / IsLockedOut / LockoutEnd; they carry
        // the 2FA counter. While a second factor is still pending a correct password must NOT reset it
        // (it would hand out a fresh set of code guesses); an expired lock is the one thing to clear.
        if (!user.TwoFactorEnabled || user.IsLockedOut)
        {
            LoginLockout.Reset(user);
        }

        if (!user.EmailConfirmed)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Please verify your email before logging in"
            };
        }

        if (!user.IsActive)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Account is inactive"
            };
        }

        if (user.TwoFactorEnabled && user.TwoFactorMethod == TwoFactorMethod.Email)
        {
            await _context.SaveChangesAsync();
            await _otpService.SendLoginTwoFactorCodeAsync(user, returnUrl);

            return new AuthResult
            {
                Success = true,
                RequiresTwoFactor = true,
                User = user
            };
        }

        user.LastLoginAt = DateTime.UtcNow;

        // Adaptive MFA: assess login risk before issuing any tokens
        var riskScore = await _riskAssessmentService.AssessLoginRiskAsync(
            user.Id, ipAddress ?? "unknown", userAgent);

        if (riskScore.Action == RiskAction.Block)
        {
            await _context.SaveChangesAsync();
            return new AuthResult
            {
                Success = false,
                Error = "Login blocked due to unusual account activity. Please contact support."
            };
        }

        if (riskScore.Action == RiskAction.StepUp)
        {
            await _context.SaveChangesAsync();
            await _otpService.SendEmailOtpAsync(user.Email, OtpPurpose.MfaVerification);

            return new AuthResult
            {
                Success = true,
                RequiresStepUp = true,
                User = user
            };
        }

        // Resolve this organization's configured token lifetime (falls back to today's
        // defaults when the user has no tenant or the tenant has no Token Configuration)
        var (accessMinutes, refreshDays) = await ResolveTokenLifetimeAsync(user.Id);

        // Apply the "Remember me" floor at issuance too, not just on the cookie -
        // otherwise a remembered session that never triggers a refresh before the
        // org's shorter default lifetime elapses gets rejected early.
        if (rememberMe)
        {
            refreshDays = Math.Max(refreshDays, AuthConstants.RememberMeMinimumDays);
        }

        // Generate refresh token first (needed for token binding)
        var refreshToken = GenerateRefreshToken();
        var refreshTokenId = Guid.NewGuid();

        // Store refresh token with device fingerprinting
        var refreshTokenEntity = new RefreshToken
        {
            Id = refreshTokenId,
            UserId = user.Id,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(refreshDays),
            IpAddress = ipAddress,  // Device fingerprinting
            UserAgent = userAgent,  // Device fingerprinting
            RememberMe = rememberMe
        };

        _context.RefreshTokens.Add(refreshTokenEntity);
        await _context.SaveChangesAsync();

        // Generate access token with token binding (binds to refresh token ID)
        var accessToken = GenerateAccessToken(user, accessMinutes, refreshTokenId);

        return new AuthResult
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            User = user,
            AccessTokenLifetimeMinutes = accessMinutes,
            RefreshTokenLifetimeDays = refreshDays
        };
    }

    public async Task<AuthResult> RefreshTokenAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
    {
        var tokenHash = HashToken(refreshToken);
        var storedToken = await _context.RefreshTokens
            .Include(rt => rt.User)
                .ThenInclude(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);

        if (storedToken == null)
        {
            return InvalidRefreshToken();
        }

        if (storedToken.IsRevoked)
        {
            // REPLAY DETECTION: a token spent by rotation is only ever held by the party that got
            // its successor, so seeing it again means the secret was copied. Which of the two
            // parties is the thief is unknowable here, so end every session of the user.
            // Tokens revoked for other reasons (logout, password reset, deactivation) are not
            // a theft signal - a stale browser may legitimately still send one of those.
            if (await _context.IsRotatedAsync(storedToken))
            {
                var revoked = await _context.RevokeRefreshTokensAsync(storedToken.UserId);

                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = storedToken.UserId,
                    Action = "RefreshTokenReplayDetected",
                    Resource = "User",
                    Details = JsonSerializer.Serialize(new { refreshTokenId = storedToken.Id, revokedTokens = revoked }),
                    IpAddress = ipAddress,
                    UserAgent = userAgent
                });
                await _context.SaveChangesAsync();

                _logger.LogWarning(
                    "Rotated refresh token replayed; all sessions of the user ended. UserId: {UserId}, " +
                    "TokenId: {TokenId}, RevokedTokens: {RevokedTokens}, ReplayIP: {ReplayIp}, ReplayUA: {ReplayUA}",
                    storedToken.UserId, storedToken.Id, revoked, ipAddress, userAgent);
            }

            return InvalidRefreshToken();
        }

        if (storedToken.IsExpired)
        {
            return InvalidRefreshToken();
        }

        // A deactivated or locked user must not keep minting access tokens. Same generic error as
        // any other bad token (no account-state oracle), and nothing is written so a user who is
        // reactivated later is not penalised for this attempt.
        if (!storedToken.User.IsActive)
        {
            return InvalidRefreshToken();
        }

        // ANOMALY DETECTION: Check if device fingerprint changed
        var ipChanged = !string.IsNullOrEmpty(storedToken.IpAddress) &&
                        !string.IsNullOrEmpty(ipAddress) &&
                        storedToken.IpAddress != ipAddress;
        var uaChanged = !string.IsNullOrEmpty(storedToken.UserAgent) &&
                        !string.IsNullOrEmpty(userAgent) &&
                        storedToken.UserAgent != userAgent;

        if (ipChanged || uaChanged)
        {
            _logger.LogWarning(
                "Refresh token used from different {Changed}. UserId: {UserId}, " +
                "OriginalIP: {OriginalIp}, NewIP: {NewIp}, " +
                "OriginalUA: {OriginalUA}, NewUA: {NewUA}",
                ipChanged && uaChanged ? "IP and UserAgent" : ipChanged ? "IP" : "UserAgent",
                storedToken.UserId,
                storedToken.IpAddress, ipAddress,
                storedToken.UserAgent, userAgent);
        }

        // Resolve this organization's configured token lifetime (falls back to today's
        // defaults when the user has no tenant or the tenant has no Token Configuration)
        var (accessMinutes, refreshDays) = await ResolveTokenLifetimeAsync(storedToken.UserId);

        // Preserve the "Remember me" floor across rotation: read the flag off the token
        // being rotated FROM (never a client-supplied value - it isn't spoofable this way),
        // and re-apply the minimum on every rotation so a remembered session never drops
        // below it just because it keeps refreshing.
        if (storedToken.RememberMe)
        {
            refreshDays = Math.Max(refreshDays, AuthConstants.RememberMeMinimumDays);
        }

        // Generate new refresh token (rotation)
        var newRefreshToken = GenerateRefreshToken();
        var newRefreshTokenId = Guid.NewGuid();

        // Store new refresh token with updated device fingerprinting
        var newRefreshTokenEntity = new RefreshToken
        {
            Id = newRefreshTokenId,
            UserId = storedToken.UserId,
            TokenHash = HashToken(newRefreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(refreshDays),
            IpAddress = ipAddress ?? storedToken.IpAddress,    // Use new IP or fall back to original
            UserAgent = userAgent ?? storedToken.UserAgent,    // Use new UA or fall back to original
            RememberMe = storedToken.RememberMe                // Carry the choice forward through rotation
        };

        // SINGLE-USE TOKENS: Revoke the old refresh token immediately; Rotate also records
        // that this revocation was a rotation, which is what replay detection above relies on.
        RefreshTokenRevocation.Rotate(storedToken, newRefreshTokenEntity);

        _context.RefreshTokens.Add(newRefreshTokenEntity);
        await _context.SaveChangesAsync();

        // Generate new access token with token binding (binds to NEW refresh token ID)
        var accessToken = GenerateAccessToken(storedToken.User, accessMinutes, newRefreshTokenId);

        return new AuthResult
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = newRefreshToken,  // Return NEW refresh token (single-use rotation)
            User = storedToken.User,
            AccessTokenLifetimeMinutes = accessMinutes,
            RefreshTokenLifetimeDays = refreshDays
        };
    }

    public async Task<bool> RevokeTokenAsync(string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);
        var storedToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);

        if (storedToken == null)
        {
            return false;
        }

        storedToken.RevokedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> VerifyEmailAsync(string token)
    {
        var tokenHash = HashVerificationToken(token);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.EmailVerificationToken == tokenHash
                                   && u.EmailVerificationTokenExpiry > DateTime.UtcNow);

        if (user == null)
        {
            return false;
        }

        user.EmailConfirmed = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationTokenExpiry = null;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SendPasswordResetAsync(string email)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
        {
            // Don't reveal if email exists
            return true;
        }

        var rawResetToken = GenerateToken();
        user.PasswordResetToken = HashVerificationToken(rawResetToken);
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);

        await _context.SaveChangesAsync();

        await _emailService.SendPasswordResetAsync(
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            rawResetToken
        );

        return true;
    }

    public async Task<bool> ResendVerificationEmailAsync(Guid userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null || user.EmailConfirmed)
            return false;

        var rawToken = GenerateToken();
        user.EmailVerificationToken = HashVerificationToken(rawToken);
        user.EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24);

        await _context.SaveChangesAsync();

        await _emailService.SendEmailVerificationAsync(
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            rawToken
        );

        return true;
    }

    public async Task<AuthResult> LoginBypassPasswordAsync(User user, string? ipAddress = null, string? userAgent = null, bool rememberMe = false)
    {
        if (!user.IsActive)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Account is inactive"
            };
        }

        // Update last login
        user.LastLoginAt = DateTime.UtcNow;
        LoginLockout.Reset(user);

        // Resolve this organization's configured token lifetime (falls back to today's
        // defaults when the user has no tenant or the tenant has no Token Configuration)
        var (accessMinutes, refreshDays) = await ResolveTokenLifetimeAsync(user.Id);

        // Apply the "Remember me" floor at issuance too, not just on the cookie -
        // otherwise a remembered session that never triggers a refresh before the
        // org's shorter default lifetime elapses gets rejected early.
        if (rememberMe)
        {
            refreshDays = Math.Max(refreshDays, AuthConstants.RememberMeMinimumDays);
        }

        // Generate refresh token
        var refreshToken = GenerateRefreshToken();
        var refreshTokenId = Guid.NewGuid();

        var refreshTokenEntity = new RefreshToken
        {
            Id = refreshTokenId,
            UserId = user.Id,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(refreshDays),
            IpAddress = ipAddress,
            UserAgent = userAgent,
            RememberMe = rememberMe
        };

        _context.RefreshTokens.Add(refreshTokenEntity);
        await _context.SaveChangesAsync();

        var accessToken = GenerateAccessToken(user, accessMinutes, refreshTokenId);

        return new AuthResult
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            User = user,
            AccessTokenLifetimeMinutes = accessMinutes,
            RefreshTokenLifetimeDays = refreshDays
        };
    }

    public async Task<AuthResult> CompletePasswordlessLoginAsync(User user, string? ipAddress = null, string? userAgent = null, string? returnUrl = null, bool rememberMe = false)
    {
        if (user.TwoFactorEnabled && user.TwoFactorMethod == TwoFactorMethod.Email)
        {
            await _otpService.SendLoginTwoFactorCodeAsync(user, returnUrl);

            return new AuthResult
            {
                Success = true,
                RequiresTwoFactor = true,
                User = user
            };
        }

        return await LoginBypassPasswordAsync(user, ipAddress, userAgent, rememberMe);
    }

    public async Task<AuthResult> VerifyStepUpAsync(string email, string code, string? ipAddress = null, string? userAgent = null, bool rememberMe = false)
    {
        var valid = await _otpService.ValidateOtpAsync(email, null, code, OtpPurpose.MfaVerification);

        if (!valid)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Invalid or expired verification code"
            };
        }

        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Invalid or expired verification code"
            };
        }

        return await LoginBypassPasswordAsync(user, ipAddress, userAgent, rememberMe);
    }

    public async Task<AuthResult> VerifyLoginTwoFactorAsync(Guid userId, string code, string? ipAddress = null, string? userAgent = null, bool rememberMe = false)
    {
        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null || !user.TwoFactorEnabled || user.TwoFactorMethod != TwoFactorMethod.Email)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Invalid or expired verification code"
            };
        }

        // Check lockout before attempting OTP verification
        if (LoginLockout.IsLocked(user))
        {
            return new AuthResult
            {
                Success = false,
                Error = AccountLockedMessage
            };
        }

        var valid = await _otpService.ValidateOtpAsync(user.Email, null, code, OtpPurpose.LoginTwoFactor);

        if (!valid)
        {
            // Track 2FA failures against the same lockout counter
            LoginLockout.RegisterFailure(user);
            if (LoginLockout.IsLocked(user))
            {
                await _context.SaveChangesAsync();
                return new AuthResult
                {
                    Success = false,
                    Error = "Too many failed attempts. Account temporarily locked."
                };
            }
            await _context.SaveChangesAsync();
            return new AuthResult
            {
                Success = false,
                Error = "Invalid or expired verification code"
            };
        }

        return await LoginBypassPasswordAsync(user, ipAddress, userAgent, rememberMe);
    }

    public async Task<bool> ResendLoginTwoFactorCodeAsync(Guid userId, string? returnUrl = null)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null || !user.TwoFactorEnabled || user.TwoFactorMethod != TwoFactorMethod.Email)
        {
            // Don't reveal account state to an unauthenticated caller
            return true;
        }

        return await _otpService.SendLoginTwoFactorCodeAsync(user, returnUrl);
    }

    public async Task<bool> ResetPasswordAsync(string token, string newPassword)
    {
        var tokenHash = HashVerificationToken(token);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.PasswordResetToken == tokenHash
                                   && u.PasswordResetTokenExpiry > DateTime.UtcNow);

        if (user == null)
        {
            return false;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;

        // Revoke all refresh tokens
        await _context.RevokeRefreshTokensAsync(user.Id);

        await _context.SaveChangesAsync();
        return true;
    }

    // One message for every way a refresh token can be unusable (unknown, revoked, rotated,
    // expired, owner inactive) so the response never reveals which one it was.
    private static AuthResult InvalidRefreshToken() => new()
    {
        Success = false,
        Error = "Invalid or expired refresh token"
    };

    private string GenerateAccessToken(User user, int expirationMinutes, Guid? refreshTokenId = null)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, $"{user.FirstName} {user.LastName}")
        };

        // TOKEN BINDING: Bind access token to refresh token ID
        if (refreshTokenId.HasValue)
        {
            claims.Add(new Claim("refresh_token_id", refreshTokenId.Value.ToString()));
        }

        // Add roles
        foreach (var userRole in user.UserRoles.WhereActive())
        {
            claims.Add(new Claim(ClaimTypes.Role, userRole.Role.Name));
        }

        var secretKey = _configuration["Jwt:SecretKey"] ?? throw new InvalidOperationException("JWT secret key not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // TTL: caller resolves this - the organization's configured Token Configuration
        // when one exists, otherwise today's default (5 minutes, down from 15, for security)
        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expirationMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GenerateRefreshToken()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// Generates a cryptographically random 256-bit token (returned as hex).
    /// The raw token is sent to the user; a SHA-256 hash is stored in the DB
    /// so a stolen DB dump cannot be used to activate/reset accounts.
    /// </summary>
    private static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string HashVerificationToken(string rawToken)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
    }

    private string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hashBytes);
    }
}
