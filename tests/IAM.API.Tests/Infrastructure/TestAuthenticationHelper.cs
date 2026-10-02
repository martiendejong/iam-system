using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace IAM.API.Tests.Infrastructure;

/// <summary>
/// Helper for generating test JWT tokens
/// </summary>
public static class TestAuthenticationHelper
{
    private const string SecretKey = "DEVELOPMENT_SECRET_KEY_CHANGE_IN_PRODUCTION_32_CHARS_MIN";
    private const string Issuer = "https://localhost:5001";
    private const string Audience = "iam-api";

    public static string GenerateJwtToken(Guid userId, string username, string[] roles)
    {
        // Root tenant from test seed data
        return GenerateJwtToken(userId, username, roles, "11111111-1111-1111-1111-111111111111");
    }

    /// <summary>
    /// Token with an explicit tenant_id claim, or NONE when <paramref name="tenantIdClaim"/> is
    /// null (the shape of a password-login token, which carries no tenant_id).
    /// </summary>
    public static string GenerateJwtToken(Guid userId, string username, string[] roles, string? tenantIdClaim)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        };

        if (tenantIdClaim != null)
        {
            claims.Add(new Claim("tenant_id", tenantIdClaim));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string GenerateAdminToken()
    {
        return GenerateJwtToken(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            "admin@test.com",
            new[] { "SystemAdmin", "TenantAdmin", "SecurityAdmin", "EmergencyAccess", "ComplianceOfficer" }
        );
    }

    public static string GenerateUserToken()
    {
        return GenerateJwtToken(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            "user@test.com",
            new[] { "User" }
        );
    }

    /// <summary>
    /// Token shaped exactly like ServiceAccountService.GenerateServiceAccountToken issues
    /// for the client_credentials grant: NO role claims, "token_type=service_account",
    /// a client_id, and one "permission" claim per granted permission (task 1496).
    /// </summary>
    public static string GenerateServiceAccountToken(string clientId, params string[] permissions)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("client_id", clientId),
            new Claim("token_type", "service_account"),
            new Claim("service_account_type", "internal")
        };
        foreach (var permission in permissions)
        {
            claims.Add(new Claim("permission", permission));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Token shaped like DeviceAuthenticationService issues for a device: "token_type=device", the
    /// device_id and (when given) the tenant_id, no roles (task 4708).
    /// </summary>
    public static string GenerateDeviceToken(string deviceId, Guid? tenantId)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, deviceId),
            new Claim("device_id", deviceId),
            new Claim("token_type", "device")
        };
        if (tenantId.HasValue)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static void AddAuthorizationHeader(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    }
}
