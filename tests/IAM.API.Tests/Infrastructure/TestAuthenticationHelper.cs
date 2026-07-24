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
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("tenant_id", "11111111-1111-1111-1111-111111111111")  // Root tenant from test seed data
        };

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

    public static void AddAuthorizationHeader(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    }
}
