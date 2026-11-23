using System.Text.Json;
using System.Text.RegularExpressions;
using OpenIddict.Abstractions;

namespace IAM.API.Mcp.OAuth;

/// <summary>
/// Constants and settings of the OAuth layer in front of the IAM MCP endpoint (docs/MCP.md).
/// IAM is already an OAuth/OIDC authorization server (OpenIddict); the MCP endpoint is simply a protected
/// resource that accepts IAM-issued access tokens carrying the narrow <see cref="Scope"/> scope. The only
/// additions are RFC 7591 dynamic client registration (so ChatGPT / Claude.ai can register themselves), an
/// RFC 8414 metadata document, an explicit consent page and the token validator.
/// </summary>
public static class McpOAuth
{
    /// <summary>The one scope dynamically registered clients may ever hold (besides offline_access).</summary>
    public const string Scope = "mcp";

    /// <summary>Audience (aud) of the access tokens: they are only meant for the IAM MCP endpoint.</summary>
    public const string Audience = "iam_mcp";

    /// <summary>Marks an OpenIddict application as created through dynamic client registration.</summary>
    public const string DynamicClientProperty = "iam_dcr";

    /// <summary>Claim on the MCP principal that tells the tool providers the caller came in via OAuth.</summary>
    public const string OAuthPrincipalClaim = "iam_mcp_oauth";

    public const string ClientIdClaim = "iam_mcp_client_id";

    public const string ClientIdPrefix = "mcp-";

    /// <summary>Kill switch: <c>Mcp:OAuth:Enabled=false</c> turns the whole OAuth layer off (DCR 404, tokens rejected).</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue("Mcp:OAuth:Enabled", true);

    /// <summary>True when the OpenIddict application was created by dynamic client registration.</summary>
    public static async Task<bool> IsDynamicClientAsync(IOpenIddictApplicationManager manager, object application)
    {
        var properties = await manager.GetPropertiesAsync(application);
        return properties.TryGetValue(DynamicClientProperty, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// The authorization server identifier advertised in the protected-resource metadata: the OpenIddict issuer
    /// (Jwt:Issuer with exactly one trailing slash, the same normalisation Program.cs applies), unless
    /// <c>Mcp:OAuth:AuthorizationServer</c> overrides it.
    /// </summary>
    public static string? AuthorizationServer(IConfiguration configuration)
    {
        var overrideValue = configuration["Mcp:OAuth:AuthorizationServer"];
        if (!string.IsNullOrWhiteSpace(overrideValue)) return overrideValue.Trim();
        var issuer = configuration["Jwt:Issuer"];
        return string.IsNullOrWhiteSpace(issuer) ? null : issuer.TrimEnd('/') + "/";
    }
}

/// <summary>
/// Allowlist of redirect URIs a dynamically registered client may use. Exact patterns, no wildcards on the host:
/// the connector callbacks of ChatGPT and Claude.ai, plus loopback URIs for local MCP clients (Claude Code, Inspector).
/// </summary>
public static class McpRedirectUriPolicy
{
    // https://chatgpt.com/connector/oauth/<one id segment>
    private static readonly Regex ChatGptConnector =
        new(@"^https://chatgpt\.com/connector/oauth/[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        "https://chatgpt.com/connector_platform_oauth_redirect",
        "https://platform.openai.com/apps-manage/oauth",
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback",
    };

    // http://localhost:<port>/<path> and http://127.0.0.1:<port>/<path> (RFC 8252 loopback), no query, no userinfo.
    private static readonly Regex Loopback =
        new(@"^http://(localhost|127\.0\.0\.1)(:[0-9]{1,5})?(/[A-Za-z0-9._~!$&'()*+,;=:@%/-]*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsAllowed(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 512) return false;
        if (uri.Contains('#') || uri.Contains('?') || uri.Contains('\\')) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (Exact.Contains(uri) || ChatGptConnector.IsMatch(uri)) return true;
        if (Loopback.IsMatch(uri))
        {
            // The port, when present, must be a real port (the regex only checked digits).
            return parsed.Port is > 0 and <= 65535;
        }
        return false;
    }
}
