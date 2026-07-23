using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAM.Infrastructure.Services;

public class OtpService : IOtpService
{
    private readonly IAMDbContext _context;
    private readonly IEmailService _emailService;
    private readonly ISmsService _smsService;
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<OtpService> _logger;

    private const int CodeLength = 6;
    private const int ExpiryMinutes = 10;
    private const int MaxAttemptsPerCode = 5;
    private const int MaxSmsPerPhonePerHour = 10;
    private const int MaxEmailPerAddressPerHour = 10;

    public OtpService(
        IAMDbContext context,
        IEmailService emailService,
        ISmsService smsService,
        IOptions<EmailSettings> emailSettings,
        ILogger<OtpService> logger)
    {
        _context = context;
        _emailService = emailService;
        _smsService = smsService;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task<bool> SendEmailOtpAsync(string email, OtpPurpose purpose)
    {
        // Rate limit: max per email per hour
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentCount = await _context.OtpCodes
            .CountAsync(c => c.Email == email && c.CreatedAt >= oneHourAgo);

        if (recentCount >= MaxEmailPerAddressPerHour)
        {
            _logger.LogWarning("Rate limit exceeded for email OTP requests: {Email}", email);
            return false;
        }

        // Find user by email (optional for OTP - some flows don't require existing user)
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

        // For login purpose, user must exist
        if (purpose == OtpPurpose.Login && user == null)
        {
            // Don't reveal if email exists
            _logger.LogDebug("Email OTP requested for non-existent email {Email}", email);
            return true;
        }

        if (purpose == OtpPurpose.Login && user != null && !user.IsActive)
        {
            _logger.LogDebug("Email OTP requested for inactive user {Email}", email);
            return true;
        }

        var code = GenerateNumericCode();
        var codeHash = HashCode(code);

        var otpCode = new OtpCode
        {
            UserId = user?.Id,
            Email = email,
            Code = codeHash,
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.AddMinutes(ExpiryMinutes),
            MaxAttempts = MaxAttemptsPerCode
        };

        _context.OtpCodes.Add(otpCode);
        await _context.SaveChangesAsync();

        // Send email with the plain code
        var username = user?.FirstName ?? "User";
        await _emailService.SendMfaCodeAsync(email, username, code);

        _logger.LogInformation("Email OTP sent to {Email} for purpose {Purpose}", email, purpose);
        return true;
    }

    public async Task<bool> SendSmsOtpAsync(string phoneNumber, OtpPurpose purpose)
    {
        // Rate limit: max per phone per hour
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentCount = await _context.OtpCodes
            .CountAsync(c => c.PhoneNumber == phoneNumber && c.CreatedAt >= oneHourAgo);

        if (recentCount >= MaxSmsPerPhonePerHour)
        {
            _logger.LogWarning("Rate limit exceeded for SMS OTP requests: {PhoneNumber}", phoneNumber);
            return false;
        }

        // Find user by phone number
        var user = await _context.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);

        if (purpose == OtpPurpose.Login && user == null)
        {
            _logger.LogDebug("SMS OTP requested for non-existent phone {PhoneNumber}", phoneNumber);
            return true;
        }

        if (purpose == OtpPurpose.Login && user != null && !user.IsActive)
        {
            _logger.LogDebug("SMS OTP requested for inactive user phone {PhoneNumber}", phoneNumber);
            return true;
        }

        var code = GenerateNumericCode();
        var codeHash = HashCode(code);

        var otpCode = new OtpCode
        {
            UserId = user?.Id,
            PhoneNumber = phoneNumber,
            Code = codeHash,
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.AddMinutes(ExpiryMinutes),
            MaxAttempts = MaxAttemptsPerCode
        };

        _context.OtpCodes.Add(otpCode);
        await _context.SaveChangesAsync();

        // Send SMS with the plain code
        var message = $"Your IAM System verification code is: {code}. It expires in {ExpiryMinutes} minutes.";
        await _smsService.SendSmsAsync(phoneNumber, message);

        _logger.LogInformation("SMS OTP sent to {PhoneNumber} for purpose {Purpose}", phoneNumber, purpose);
        return true;
    }

    public async Task<bool> ValidateOtpAsync(string? email, string? phoneNumber, string code, OtpPurpose purpose)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phoneNumber))
        {
            return false;
        }

        var codeHash = HashCode(code);

        // Find the most recent matching OTP
        var query = _context.OtpCodes
            .Where(c => c.Purpose == purpose && c.UsedAt == null && c.ExpiresAt > DateTime.UtcNow);

        if (!string.IsNullOrWhiteSpace(email))
        {
            query = query.Where(c => c.Email == email);
        }
        else
        {
            query = query.Where(c => c.PhoneNumber == phoneNumber);
        }

        var otpCode = await query
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (otpCode == null)
        {
            _logger.LogDebug("OTP validation failed: no matching code found");
            return false;
        }

        // Check max attempts
        if (otpCode.Attempts >= otpCode.MaxAttempts)
        {
            _logger.LogWarning("OTP validation failed: max attempts exceeded");
            return false;
        }

        // Increment attempts
        otpCode.Attempts++;

        // Verify hash
        if (otpCode.Code != codeHash)
        {
            await _context.SaveChangesAsync();
            _logger.LogDebug("OTP validation failed: code mismatch (attempt {Attempt}/{Max})",
                otpCode.Attempts, otpCode.MaxAttempts);
            return false;
        }

        // Mark as used
        otpCode.UsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("OTP validated successfully for {Target} purpose {Purpose}",
            email ?? phoneNumber, purpose);
        return true;
    }

    public async Task<bool> SendLoginTwoFactorCodeAsync(User user)
    {
        // Rate limit: max per email per hour (shared budget with other email OTP purposes)
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentCount = await _context.OtpCodes
            .CountAsync(c => c.Email == user.Email && c.Purpose == OtpPurpose.LoginTwoFactor && c.CreatedAt >= oneHourAgo);

        if (recentCount >= MaxEmailPerAddressPerHour)
        {
            _logger.LogWarning("Rate limit exceeded for login 2FA code requests: {Email}", user.Email);
            return false;
        }

        var code = GenerateNumericCode();
        var codeHash = HashCode(code);

        var otpCode = new OtpCode
        {
            UserId = user.Id,
            Email = user.Email,
            Code = codeHash,
            Purpose = OtpPurpose.LoginTwoFactor,
            ExpiresAt = DateTime.UtcNow.AddMinutes(ExpiryMinutes),
            MaxAttempts = MaxAttemptsPerCode
        };

        _context.OtpCodes.Add(otpCode);
        await _context.SaveChangesAsync();

        var baseUrl = _emailSettings.BaseUrl?.TrimEnd('/');
        var verifyUrl = $"{baseUrl}/verify-2fa?userId={user.Id}&code={code}";

        await _emailService.SendLoginTwoFactorCodeAsync(user.Email, user.FirstName, code, verifyUrl);

        _logger.LogInformation("Login 2FA code sent to {Email}", user.Email);
        return true;
    }

    public async Task<int> CleanupExpiredCodesAsync()
    {
        var expiredCodes = await _context.OtpCodes
            .Where(c => c.ExpiresAt < DateTime.UtcNow || c.UsedAt != null)
            .Where(c => c.CreatedAt < DateTime.UtcNow.AddDays(-1))
            .ToListAsync();

        _context.OtpCodes.RemoveRange(expiredCodes);
        await _context.SaveChangesAsync();

        if (expiredCodes.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired OTP codes", expiredCodes.Count);
        }

        return expiredCodes.Count;
    }

    private static string GenerateNumericCode()
    {
        // Generate cryptographically random 6-digit code
        var randomBytes = new byte[4];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        var number = BitConverter.ToUInt32(randomBytes, 0) % 1_000_000;
        return number.ToString("D6");
    }

    private static string HashCode(string code)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToBase64String(hashBytes);
    }
}
