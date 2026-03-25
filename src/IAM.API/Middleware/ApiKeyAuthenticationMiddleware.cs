using System.Security.Claims;
using System.Text.Json;
using IAM.Core.Services;

namespace IAM.API.Middleware;

/// <summary>
/// Middleware that authenticates requests using the X-API-Key header.
/// If a valid API key is present, creates a ClaimsPrincipal with the key's permissions.
/// If no header is present, passes through to let JWT authentication handle it.
/// If the header is present but invalid, returns 401 Unauthorized.
/// </summary>
public class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;
    private const string ApiKeyHeaderName = "X-API-Key";

    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IApiKeyService apiKeyService)
    {
        // Only process if the X-API-Key header is present
        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeader))
        {
            // No API key header - pass through to JWT auth
            await _next(context);
            return;
        }

        var rawKey = apiKeyHeader.ToString();

        if (string.IsNullOrWhiteSpace(rawKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"Unauthorized","message":"API key header is empty."}""");
            return;
        }

        // Validate the key via the service (hashes input, checks DB)
        var apiKey = await apiKeyService.ValidateApiKeyAsync(rawKey, context.RequestAborted);

        if (apiKey == null)
        {
            _logger.LogWarning("Invalid API key attempt from {IpAddress}",
                context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"Unauthorized","message":"Invalid or expired API key."}""");
            return;
        }

        // Check IP allowlist if configured
        if (!string.IsNullOrEmpty(apiKey.AllowedIps))
        {
            var allowedIps = JsonSerializer.Deserialize<List<string>>(apiKey.AllowedIps);
            var clientIp = context.Connection.RemoteIpAddress?.ToString();

            if (allowedIps != null && allowedIps.Count > 0 && clientIp != null)
            {
                if (!allowedIps.Contains(clientIp))
                {
                    _logger.LogWarning(
                        "API key {KeyPrefix} used from unauthorized IP {IpAddress}",
                        apiKey.KeyPrefix, clientIp);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"error":"Forbidden","message":"IP address not allowed for this API key."}""");
                    return;
                }
            }
        }

        // Build claims from the API key
        var claims = new List<Claim>
        {
            new("api_key_id", apiKey.Id.ToString()),
            new("api_key_name", apiKey.Name),
            new("api_key_prefix", apiKey.KeyPrefix),
            new("auth_method", "api_key")
        };

        // Add user claims if the key is associated with a user
        if (apiKey.UserId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, apiKey.UserId.Value.ToString()));
            claims.Add(new Claim("sub", apiKey.UserId.Value.ToString()));

            if (apiKey.User != null)
            {
                claims.Add(new Claim(ClaimTypes.Email, apiKey.User.Email));
                claims.Add(new Claim(ClaimTypes.Name, $"{apiKey.User.FirstName} {apiKey.User.LastName}"));
            }
        }

        // Add tenant claim
        if (apiKey.TenantId.HasValue)
        {
            claims.Add(new Claim("tenant_id", apiKey.TenantId.Value.ToString()));
        }

        // Add permission claims
        var permissions = JsonSerializer.Deserialize<List<string>>(apiKey.Permissions);
        if (permissions != null)
        {
            foreach (var permission in permissions)
            {
                claims.Add(new Claim("permission", permission));
            }
        }

        // Create the principal and set it on the context
        var identity = new ClaimsIdentity(claims, "ApiKey");
        context.User = new ClaimsPrincipal(identity);

        // Store API key metadata in HttpContext.Items for rate limiting middleware
        context.Items["ApiKeyPrefix"] = apiKey.KeyPrefix;
        if (apiKey.RateLimitPerMinute.HasValue)
        {
            context.Items["ApiKeyRateLimit"] = apiKey.RateLimitPerMinute.Value;
        }

        _logger.LogDebug(
            "API key {KeyPrefix} authenticated successfully for user {UserId}",
            apiKey.KeyPrefix, apiKey.UserId);

        await _next(context);
    }
}

/// <summary>
/// Extension method for clean middleware registration.
/// </summary>
public static class ApiKeyAuthenticationMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ApiKeyAuthenticationMiddleware>();
    }
}
