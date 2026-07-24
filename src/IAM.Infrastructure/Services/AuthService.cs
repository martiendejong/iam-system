using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly IRiskAssessmentService _riskAssessmentService;
    private readonly IOtpService _otpService;

    public AuthService(
        IAMDbContext context,
        IConfiguration configuration,
        IEmailService emailService,
        IRiskAssessmentService riskAssessmentService,
        IOtpService otpService)
    {
        _context = context;
        _configuration = configuration;
        _emailService = emailService;
        _riskAssessmentService = riskAssessmentService;
        _otpService = otpService;
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

        // Create user
        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FirstName = firstName,
            LastName = lastName,
            EmailConfirmed = false,
            EmailVerificationToken = GenerateToken(),
            EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24),
            IsActive = true
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        await _emailService.SendEmailVerificationAsync(
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            user.EmailVerificationToken!
        );

        return new AuthResult
        {
            Success = true,
            User = user
        };
    }

    public async Task<AuthResult> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null)
    {
        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Invalid email or password"
            };
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            // Increment failed login attempts
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= 5)
            {
                user.IsLockedOut = true;
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(15);
            }
            await _context.SaveChangesAsync();

            return new AuthResult
            {
                Success = false,
                Error = "Invalid email or password"
            };
        }

        if (user.IsLockedOut && user.LockoutEnd > DateTime.UtcNow)
        {
            return new AuthResult
            {
                Success = false,
                Error = $"Account locked. Try again after {user.LockoutEnd:yyyy-MM-dd HH:mm:ss} UTC"
            };
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

        // Reset failed attempts (password + account checks passed)
        user.FailedLoginAttempts = 0;
        user.IsLockedOut = false;
        user.LockoutEnd = null;

        if (user.TwoFactorEnabled && user.TwoFactorMethod == TwoFactorMethod.Email)
        {
            await _context.SaveChangesAsync();
            await _otpService.SendLoginTwoFactorCodeAsync(user);

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

        // Generate refresh token first (needed for token binding)
        var refreshToken = GenerateRefreshToken();
        var refreshTokenId = Guid.NewGuid();

        // Store refresh token with device fingerprinting
        var refreshTokenEntity = new RefreshToken
        {
            Id = refreshTokenId,
            UserId = user.Id,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IpAddress = ipAddress,  // Device fingerprinting
            UserAgent = userAgent   // Device fingerprinting
        };

        _context.RefreshTokens.Add(refreshTokenEntity);
        await _context.SaveChangesAsync();

        // Generate access token with token binding (binds to refresh token ID)
        var accessToken = GenerateAccessToken(user, refreshTokenId);

        return new AuthResult
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            User = user
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

        if (storedToken == null || !storedToken.IsActive)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Invalid or expired refresh token"
            };
        }

        // ANOMALY DETECTION: Check if device fingerprint changed
        if (!string.IsNullOrEmpty(storedToken.IpAddress) && !string.IsNullOrEmpty(ipAddress))
        {
            if (storedToken.IpAddress != ipAddress)
            {
                // IP address changed - potential token theft
                // Log this as suspicious activity (TODO: Add logging)
                // For now, we'll allow it but could add stricter policies
            }
        }

        if (!string.IsNullOrEmpty(storedToken.UserAgent) && !string.IsNullOrEmpty(userAgent))
        {
            if (storedToken.UserAgent != userAgent)
            {
                // User agent changed - potential token theft
                // This is more suspicious than IP change (VPN, mobile network switching)
                // Log this as suspicious activity (TODO: Add logging)
            }
        }

        // SINGLE-USE TOKENS: Revoke the old refresh token immediately
        storedToken.RevokedAt = DateTime.UtcNow;

        // Generate new refresh token (rotation)
        var newRefreshToken = GenerateRefreshToken();
        var newRefreshTokenId = Guid.NewGuid();

        // Store new refresh token with updated device fingerprinting
        var newRefreshTokenEntity = new RefreshToken
        {
            Id = newRefreshTokenId,
            UserId = storedToken.UserId,
            TokenHash = HashToken(newRefreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IpAddress = ipAddress ?? storedToken.IpAddress,    // Use new IP or fall back to original
            UserAgent = userAgent ?? storedToken.UserAgent     // Use new UA or fall back to original
        };

        _context.RefreshTokens.Add(newRefreshTokenEntity);
        await _context.SaveChangesAsync();

        // Generate new access token with token binding (binds to NEW refresh token ID)
        var accessToken = GenerateAccessToken(storedToken.User, newRefreshTokenId);

        return new AuthResult
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = newRefreshToken,  // Return NEW refresh token (single-use rotation)
            User = storedToken.User
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
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.EmailVerificationToken == token
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

        user.PasswordResetToken = GenerateToken();
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);

        await _context.SaveChangesAsync();

        await _emailService.SendPasswordResetAsync(
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            user.PasswordResetToken!
        );

        return true;
    }

    public async Task<bool> ResendVerificationEmailAsync(Guid userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null || user.EmailConfirmed)
            return false;

        user.EmailVerificationToken = GenerateToken();
        user.EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24);

        await _context.SaveChangesAsync();

        await _emailService.SendEmailVerificationAsync(
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            user.EmailVerificationToken!
        );

        return true;
    }

    public async Task<AuthResult> LoginBypassPasswordAsync(User user, string? ipAddress = null, string? userAgent = null)
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
        user.FailedLoginAttempts = 0;
        user.IsLockedOut = false;
        user.LockoutEnd = null;

        // Generate refresh token
        var refreshToken = GenerateRefreshToken();
        var refreshTokenId = Guid.NewGuid();

        var refreshTokenEntity = new RefreshToken
        {
            Id = refreshTokenId,
            UserId = user.Id,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IpAddress = ipAddress,
            UserAgent = userAgent
        };

        _context.RefreshTokens.Add(refreshTokenEntity);
        await _context.SaveChangesAsync();

        var accessToken = GenerateAccessToken(user, refreshTokenId);

        return new AuthResult
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            User = user
        };
    }

    public async Task<AuthResult> VerifyStepUpAsync(string email, string code, string? ipAddress = null, string? userAgent = null)
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

        return await LoginBypassPasswordAsync(user, ipAddress, userAgent);
    }

    public async Task<AuthResult> VerifyLoginTwoFactorAsync(Guid userId, string code, string? ipAddress = null, string? userAgent = null)
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

        var valid = await _otpService.ValidateOtpAsync(user.Email, null, code, OtpPurpose.LoginTwoFactor);

        if (!valid)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Invalid or expired verification code"
            };
        }

        return await LoginBypassPasswordAsync(user, ipAddress, userAgent);
    }

    public async Task<bool> ResendLoginTwoFactorCodeAsync(Guid userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null || !user.TwoFactorEnabled || user.TwoFactorMethod != TwoFactorMethod.Email)
        {
            // Don't reveal account state to an unauthenticated caller
            return true;
        }

        return await _otpService.SendLoginTwoFactorCodeAsync(user);
    }

    public async Task<bool> ResetPasswordAsync(string token, string newPassword)
    {
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.PasswordResetToken == token
                                   && u.PasswordResetTokenExpiry > DateTime.UtcNow);

        if (user == null)
        {
            return false;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;

        // Revoke all refresh tokens
        var tokens = await _context.RefreshTokens
            .Where(rt => rt.UserId == user.Id && rt.RevokedAt == null)
            .ToListAsync();

        foreach (var t in tokens)
        {
            t.RevokedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return true;
    }

    private string GenerateAccessToken(User user, Guid? refreshTokenId = null)
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
        foreach (var userRole in user.UserRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, userRole.Role.Name));
        }

        var secretKey = _configuration["Jwt:SecretKey"] ?? throw new InvalidOperationException("JWT secret key not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // REDUCED TTL: Default 5 minutes (down from 15) for better security
        var expirationMinutes = int.Parse(_configuration["Jwt:AccessTokenExpirationMinutes"] ?? "5");

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

    private string GenerateToken()
    {
        return Guid.NewGuid().ToString("N");
    }

    private string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hashBytes);
    }
}
