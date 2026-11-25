using Microsoft.AspNetCore.Mvc;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Mcp.OAuth;

/// <summary>
/// RFC 8414 authorization server metadata, as MCP clients look it up. OpenIddict only publishes the OIDC
/// <c>/.well-known/openid-configuration</c> document; the MCP authorization spec makes clients try
/// <c>/.well-known/oauth-authorization-server[/&lt;issuer path&gt;]</c> first, and it needs a
/// <c>registration_endpoint</c> and <c>none</c> as a token endpoint auth method, so this serves the MCP view of IAM:
/// authorization code + PKCE S256, public clients, the narrow <c>mcp</c> scope, dynamic client registration.
/// (The full OIDC document keeps describing everything else IAM does, and now also carries registration_endpoint.)
/// </summary>
[ApiController]
public class McpAuthorizationServerMetadataController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public McpAuthorizationServerMetadataController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("/.well-known/oauth-authorization-server")]
    [HttpGet("/.well-known/oauth-authorization-server/{**issuerPath}")]
    [Produces("application/json")]
    public IActionResult Get(string? issuerPath = null)
    {
        if (!McpOAuth.IsEnabled(_configuration)) return NotFound();

        var issuer = McpOAuth.AuthorizationServer(_configuration)
            ?? $"{Request.Scheme}://{Request.Host}/";

        // Endpoints live at the host root (the reverse proxy strips an /auth prefix before Kestrel), exactly as in the
        // OpenIddict discovery document.
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var doc = new Dictionary<string, object?>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = $"{baseUrl}/connect/authorize",
            ["token_endpoint"] = $"{baseUrl}/connect/token",
            ["registration_endpoint"] = $"{baseUrl}/connect/register",
            ["revocation_endpoint"] = $"{baseUrl}/connect/revoke",
            ["jwks_uri"] = $"{baseUrl}/.well-known/jwks",
            ["response_types_supported"] = new[] { ResponseTypes.Code },
            ["grant_types_supported"] = new[] { GrantTypes.AuthorizationCode, GrantTypes.RefreshToken },
            ["code_challenge_methods_supported"] = new[] { CodeChallengeMethods.Sha256 },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["revocation_endpoint_auth_methods_supported"] = new[] { "none" },
            ["scopes_supported"] = new[] { McpOAuth.Scope, Scopes.OfflineAccess },
        };
        return Ok(doc);
    }
}
