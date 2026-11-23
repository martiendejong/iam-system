using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Mcp.OAuth;

/// <summary>
/// RFC 7591 dynamic client registration for MCP connectors (ChatGPT, Claude.ai, local MCP clients).
/// Anonymous by design (that is what DCR is), so the blast radius is fixed server side, whatever the request says:
/// <list type="bullet">
/// <item>public client (no secret), authorization_code + refresh_token only, response_type code, PKCE required;</item>
/// <item>the only scopes ever granted are <c>mcp</c> and <c>offline_access</c>;</item>
/// <item>redirect URIs must match <see cref="McpRedirectUriPolicy"/> (ChatGPT / Claude connector callbacks, loopback);</item>
/// <item>no client credentials / password / device grants, no post-logout redirects;</item>
/// <item>a per-IP and a global cap on registrations, so the endpoint cannot be used to fill the database.</item>
/// </list>
/// The created application carries <see cref="McpOAuth.DynamicClientProperty"/>, which the authorization endpoint uses to
/// require explicit consent and to strip role/tenant/profile claims out of the tokens it mints for it.
/// </summary>
[ApiController]
[Route("connect")]
public class DynamicClientRegistrationController : ControllerBase
{
    private const int MaxRedirectUris = 5;
    private const int MaxClientNameLength = 100;

    private readonly IOpenIddictApplicationManager _applications;
    private readonly IAMDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DynamicClientRegistrationController> _logger;

    public DynamicClientRegistrationController(
        IOpenIddictApplicationManager applications,
        IAMDbContext db,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<DynamicClientRegistrationController> logger)
    {
        _applications = applications;
        _db = db;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public sealed class RegistrationRequest
    {
        [JsonPropertyName("redirect_uris")] public List<string>? RedirectUris { get; set; }
        [JsonPropertyName("client_name")] public string? ClientName { get; set; }
        [JsonPropertyName("grant_types")] public List<string>? GrantTypes { get; set; }
        [JsonPropertyName("response_types")] public List<string>? ResponseTypes { get; set; }
        [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }

    [HttpPost("register")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<IActionResult> Register([FromBody] RegistrationRequest? request, CancellationToken ct)
    {
        if (!McpOAuth.IsEnabled(_configuration)) return NotFound();
        if (request is null) return Error("invalid_client_metadata", "A JSON registration request is required.");

        // --- throttle first, so rejected requests are cheap -------------------------------------------------------
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var perIpLimit = _configuration.GetValue("Mcp:OAuth:RegistrationsPerIpPerHour", 30);
        var counterKey = $"iam:mcp-dcr:{ip}";
        var used = _cache.TryGetValue(counterKey, out int current) ? current : 0;
        if (used >= perIpLimit)
        {
            Response.Headers.RetryAfter = "3600";
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "temporarily_unavailable", error_description = "Too many client registrations from this address." });
        }

        var maxClients = _configuration.GetValue("Mcp:OAuth:MaxDynamicClients", 1000);
        var existing = await _db.Set<OpenIddictEntityFrameworkCoreApplication>()
            .CountAsync(a => a.Properties != null && a.Properties.Contains(McpOAuth.DynamicClientProperty), ct);
        if (existing >= maxClients)
        {
            _logger.LogWarning("MCP dynamic client registration refused: {Count} dynamic clients already exist (cap {Cap})", existing, maxClients);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "temporarily_unavailable", error_description = "The client registration limit has been reached." });
        }

        // --- validate: anything outside the narrow profile is rejected, never silently widened --------------------
        var redirectUris = request.RedirectUris?.Select(u => u?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
        if (redirectUris.Count == 0)
            return Error("invalid_redirect_uri", "redirect_uris is required.");
        if (redirectUris.Count > MaxRedirectUris)
            return Error("invalid_redirect_uri", $"At most {MaxRedirectUris} redirect URIs are accepted.");
        foreach (var uri in redirectUris)
        {
            if (!McpRedirectUriPolicy.IsAllowed(uri))
                return Error("invalid_redirect_uri", "This redirect URI is not allowed for MCP connectors.");
        }

        var grantTypes = request.GrantTypes is { Count: > 0 } ? request.GrantTypes : new List<string> { GrantTypes.AuthorizationCode, GrantTypes.RefreshToken };
        if (grantTypes.Any(g => g != GrantTypes.AuthorizationCode && g != GrantTypes.RefreshToken) || !grantTypes.Contains(GrantTypes.AuthorizationCode))
            return Error("invalid_client_metadata", "Only the authorization_code and refresh_token grant types are supported.");

        var responseTypes = request.ResponseTypes is { Count: > 0 } ? request.ResponseTypes : new List<string> { ResponseTypes.Code };
        if (responseTypes.Any(r => r != ResponseTypes.Code))
            return Error("invalid_client_metadata", "Only the code response type is supported.");

        // RFC 7591 3.2.1: the server may substitute values it does not support. Public clients with PKCE are the only
        // kind registered here, so a requested client_secret_* method is answered with "none" instead of an error.
        var requestedScopes = (request.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requestedScopes.Any(s => s != McpOAuth.Scope && s != Scopes.OfflineAccess))
            return Error("invalid_client_metadata", "Only the 'mcp' and 'offline_access' scopes can be registered.");

        var clientName = SanitizeName(request.ClientName);

        // --- create -----------------------------------------------------------------------------------------------
        var clientId = McpOAuth.ClientIdPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            DisplayName = clientName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + McpOAuth.Scope,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var uri in redirectUris) descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
        descriptor.Properties[McpOAuth.DynamicClientProperty] = JsonSerializer.SerializeToElement(true);
        descriptor.Properties["iam_dcr_registered_at"] = JsonSerializer.SerializeToElement(DateTime.UtcNow);

        await _applications.CreateAsync(descriptor, ct);
        _cache.Set(counterKey, used + 1, TimeSpan.FromHours(1));
        _logger.LogInformation("MCP dynamic client {ClientId} registered ({Name}) with {Count} redirect URI(s)", clientId, clientName, redirectUris.Count);

        var body = new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["client_name"] = clientName,
            ["redirect_uris"] = redirectUris,
            ["grant_types"] = new[] { GrantTypes.AuthorizationCode, GrantTypes.RefreshToken },
            ["response_types"] = new[] { ResponseTypes.Code },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = $"{McpOAuth.Scope} {Scopes.OfflineAccess}",
        };
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, body);
    }

    private IActionResult Error(string error, string description) =>
        BadRequest(new { error, error_description = description });

    /// <summary>Plain text only: control characters and markup characters are dropped, length is capped.</summary>
    internal static string SanitizeName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>' && c != '"' && c != '&').ToArray()).Trim();
        if (cleaned.Length > MaxClientNameLength) cleaned = cleaned[..MaxClientNameLength];
        return cleaned.Length == 0 ? "MCP client" : cleaned;
    }
}
