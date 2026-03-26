using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Infrastructure.Services;

public class ServiceAccountService : IServiceAccountService
{
    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;

    public ServiceAccountService(IAMDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public async Task<(ServiceAccount Account, string RawClientSecret)> CreateAsync(
        string name,
        Guid? tenantId,
        ServiceAccountType type,
        List<string>? permissions = null,
        string? certificateThumbprint = null,
        string? description = null,
        CancellationToken ct = default)
    {
        // Generate a unique client ID
        var clientId = $"svc_{GenerateRandomAlphanumeric(24)}";

        // Generate a cryptographically secure client secret
        var rawSecret = GenerateClientSecret();
        var secretHash = ComputeSha256Hash(rawSecret);

        var account = new ServiceAccount
        {
            Id = Guid.NewGuid(),
            Name = name,
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecretHash = secretHash,
            Permissions = permissions != null ? JsonSerializer.Serialize(permissions) : "[]",
            Type = type,
            IsActive = true,
            CertificateThumbprint = certificateThumbprint,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.ServiceAccounts.Add(account);
        await _context.SaveChangesAsync(ct);

        return (account, rawSecret);
    }

    public async Task<ServiceAccount?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.ServiceAccounts
            .Include(sa => sa.Tenant)
            .FirstOrDefaultAsync(sa => sa.Id == id, ct);
    }

    public async Task<ServiceAccount?> GetByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        return await _context.ServiceAccounts
            .Include(sa => sa.Tenant)
            .FirstOrDefaultAsync(sa => sa.ClientId == clientId, ct);
    }

    public async Task<List<ServiceAccount>> ListAsync(
        Guid? tenantId = null,
        ServiceAccountType? type = null,
        bool? isActive = null,
        CancellationToken ct = default)
    {
        var query = _context.ServiceAccounts
            .Include(sa => sa.Tenant)
            .AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(sa => sa.TenantId == tenantId.Value);

        if (type.HasValue)
            query = query.Where(sa => sa.Type == type.Value);

        if (isActive.HasValue)
            query = query.Where(sa => sa.IsActive == isActive.Value);

        return await query
            .OrderByDescending(sa => sa.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ServiceAccount?> UpdateAsync(
        Guid id,
        string? name = null,
        List<string>? permissions = null,
        ServiceAccountType? type = null,
        string? certificateThumbprint = null,
        string? description = null,
        bool? isActive = null,
        CancellationToken ct = default)
    {
        var account = await _context.ServiceAccounts.FindAsync(new object[] { id }, ct);
        if (account == null) return null;

        if (name != null) account.Name = name;
        if (permissions != null) account.Permissions = JsonSerializer.Serialize(permissions);
        if (type.HasValue) account.Type = type.Value;
        if (certificateThumbprint != null) account.CertificateThumbprint = certificateThumbprint;
        if (description != null) account.Description = description;
        if (isActive.HasValue) account.IsActive = isActive.Value;

        account.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return account;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _context.ServiceAccounts.FindAsync(new object[] { id }, ct);
        if (account == null) return false;

        _context.ServiceAccounts.Remove(account);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Success, string? NewRawSecret)> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _context.ServiceAccounts.FindAsync(new object[] { id }, ct);
        if (account == null) return (false, null);

        var newRawSecret = GenerateClientSecret();
        account.ClientSecretHash = ComputeSha256Hash(newRawSecret);
        account.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return (true, newRawSecret);
    }

    public async Task<(bool Success, string? AccessToken, DateTime? ExpiresAt)> AuthenticateAsync(
        string clientId,
        string clientSecret,
        CancellationToken ct = default)
    {
        var account = await _context.ServiceAccounts
            .FirstOrDefaultAsync(sa => sa.ClientId == clientId, ct);

        if (account == null || !account.IsActive)
            return (false, null, null);

        // Verify the client secret
        var secretHash = ComputeSha256Hash(clientSecret);
        if (account.ClientSecretHash != secretHash)
            return (false, null, null);

        // Update last authenticated timestamp
        account.LastAuthenticatedAt = DateTime.UtcNow;
        try { await _context.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* Best-effort update */ }

        // Generate access token
        var expiresAt = DateTime.UtcNow.AddMinutes(
            int.Parse(_configuration["Jwt:ServiceAccountTokenExpirationMinutes"] ?? "30"));

        var accessToken = GenerateServiceAccountToken(account, expiresAt);
        return (true, accessToken, expiresAt);
    }

    public async Task<ServiceAccount?> ValidateMtlsAsync(string certificateThumbprint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(certificateThumbprint))
            return null;

        var account = await _context.ServiceAccounts
            .Include(sa => sa.Tenant)
            .FirstOrDefaultAsync(sa =>
                sa.CertificateThumbprint == certificateThumbprint &&
                sa.IsActive,
                ct);

        if (account != null)
        {
            account.LastAuthenticatedAt = DateTime.UtcNow;
            try { await _context.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { /* Best-effort */ }
        }

        return account;
    }

    public async Task<(bool Success, string? AccessToken, DateTime? ExpiresAt)> ExchangeTokenAsync(
        string subjectToken,
        string targetService,
        string? scopes = null,
        CancellationToken ct = default)
    {
        // Validate the subject token
        var principal = ValidateToken(subjectToken);
        if (principal == null)
            return (false, null, null);

        var subjectIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? principal.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(subjectIdClaim) || !Guid.TryParse(subjectIdClaim, out var subjectId))
            return (false, null, null);

        // Generate the exchanged token with reduced scope
        var expiresAt = DateTime.UtcNow.AddMinutes(
            int.Parse(_configuration["Jwt:TokenExchangeExpirationMinutes"] ?? "15"));

        var exchangedToken = GenerateExchangedToken(subjectId, targetService, scopes, expiresAt);

        // Record the exchange for audit
        var exchange = new TokenExchange
        {
            Id = Guid.NewGuid(),
            OriginalTokenHash = ComputeSha256Hash(subjectToken),
            ExchangedTokenHash = ComputeSha256Hash(exchangedToken),
            SubjectId = subjectId,
            TargetService = targetService,
            Scopes = scopes,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        };

        _context.TokenExchanges.Add(exchange);
        await _context.SaveChangesAsync(ct);

        return (true, exchangedToken, expiresAt);
    }

    // ---- Private helpers ----

    private string GenerateServiceAccountToken(ServiceAccount account, DateTime expiresAt)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim("client_id", account.ClientId),
            new Claim("token_type", "service_account"),
            new Claim("service_account_type", account.Type.ToString().ToLowerInvariant())
        };

        if (account.TenantId.HasValue)
            claims.Add(new Claim("tenant_id", account.TenantId.Value.ToString()));

        // Add permissions as individual claims
        var permissions = JsonSerializer.Deserialize<List<string>>(account.Permissions) ?? new();
        foreach (var perm in permissions)
        {
            claims.Add(new Claim("permission", perm));
        }

        var secretKey = _configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("JWT secret key not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GenerateExchangedToken(Guid subjectId, string targetService, string? scopes, DateTime expiresAt)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, subjectId.ToString()),
            new Claim("token_type", "token_exchange"),
            new Claim("act_as", subjectId.ToString()),
            new Claim("target_service", targetService)
        };

        if (!string.IsNullOrEmpty(scopes))
        {
            foreach (var scope in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim("scope", scope));
            }
        }

        var secretKey = _configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("JWT secret key not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: targetService, // Audience is the target service
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var secretKey = _configuration["Jwt:SecretKey"]
                ?? throw new InvalidOperationException("JWT secret key not configured");

            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(secretKey);

            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = false, // Token exchange accepts any audience
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);

            return principal;
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateClientSecret()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return $"svc_secret_{Base64UrlEncode(randomBytes)}";
    }

    private static string GenerateRandomAlphanumeric(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var result = new char[length];
        using var rng = RandomNumberGenerator.Create();
        var buffer = new byte[length];
        rng.GetBytes(buffer);
        for (int i = 0; i < length; i++)
        {
            result[i] = chars[buffer[i] % chars.Length];
        }
        return new string(result);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
