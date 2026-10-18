using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jengo.Mcp;

namespace IAM.API.Mcp;

/// <summary>
/// One configured MCP API key, mapped to the IAM user the key acts as (task 5800).
/// The plaintext key is never stored: only its lowercase-hex SHA-256 goes in configuration
/// (<c>Mcp:Keys[].Sha256</c>), exactly like the shared template's own configured key store.
/// These keys are deliberately separate from IAM's own <c>iam_</c> API keys (Hazina store):
/// an MCP key only opens the /mcp tool surface, never the REST API.
/// </summary>
public sealed class IamMcpKey
{
    /// <summary>Stable, non-secret label for the key (shown in logs as the caller name).</summary>
    public string Id { get; set; } = "";

    /// <summary>Lowercase hex SHA-256 of the key. Generate with <see cref="Hash"/>.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>The IAM user id (Guid) this key acts as. Becomes the
    /// <see cref="ClaimTypes.NameIdentifier"/> claim; every tool call resolves it against the
    /// Users table, so a key bound to a missing or deactivated user cannot do anything.</summary>
    public string Subject { get; set; } = "";

    /// <summary>Display name / email surfaced in logs (optional, non-authoritative).</summary>
    public string? Name { get; set; }
    public string? Email { get; set; }

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>
/// Validates the API key presented to <c>/mcp</c> (<c>X-Api-Key</c>, or as a bearer token — the
/// form ChatGPT connectors send) against the keys in the <c>Mcp:Keys</c> configuration array.
/// Each key is bound to one IAM user, so a valid key authenticates as exactly that user; the
/// tool provider then re-checks that the user exists and is active. No new DB table is
/// introduced: keys live in configuration, like the template's own key store and the
/// Jengo Calendar MCP server this mirrors.
/// </summary>
public sealed class IamMcpApiKeyValidator : IMcpApiKeyValidator
{
    private readonly IReadOnlyList<IamMcpKey> _keys;

    public IamMcpApiKeyValidator(IConfiguration configuration)
    {
        _keys = configuration.GetSection("Mcp:Keys").Get<List<IamMcpKey>>() ?? new();
    }

    public ValueTask<ClaimsPrincipal?> ValidateAsync(string apiKey, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return ValueTask.FromResult<ClaimsPrincipal?>(null);

        var presented = Encoding.UTF8.GetBytes(IamMcpKey.Hash(apiKey));
        foreach (var key in _keys)
        {
            if (string.IsNullOrEmpty(key.Sha256) || string.IsNullOrEmpty(key.Subject)) continue;
            var stored = Encoding.UTF8.GetBytes(key.Sha256.ToLowerInvariant());
            // Constant-time compare so a wrong key leaks nothing about how close it was.
            if (presented.Length != stored.Length) continue;
            if (!CryptographicOperations.FixedTimeEquals(presented, stored)) continue;

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, key.Subject),
                new("sub", key.Subject),
            };
            if (!string.IsNullOrWhiteSpace(key.Name)) claims.Add(new Claim(ClaimTypes.Name, key.Name));
            if (!string.IsNullOrWhiteSpace(key.Email)) claims.Add(new Claim(ClaimTypes.Email, key.Email));

            var principal = McpPrincipal.Create(
                authenticationType: "IamMcpApiKey",
                name: string.IsNullOrWhiteSpace(key.Name) ? key.Id : key.Name!,
                scopes: new[] { "iam" },
                extraClaims: claims);
            return ValueTask.FromResult<ClaimsPrincipal?>(principal);
        }

        return ValueTask.FromResult<ClaimsPrincipal?>(null);
    }
}
