namespace IAM.API.Middleware;

/// <summary>
/// Adds one fixed set of security response headers to every response (HTML, static files, API, redirects, errors).
/// Headers are written in OnStarting so they are present whatever later middleware produces the response.
/// Framing is denied by default; origins listed in Security:AllowedFrameAncestors may frame IAM, in which case
/// X-Frame-Options is dropped (it cannot express an allow-list) and CSP frame-ancestors carries the list.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _csp;
    private readonly bool _denyFraming;

    public SecurityHeadersMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        var origins = (configuration.GetSection("Security:AllowedFrameAncestors").Get<string[]>() ?? Array.Empty<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o) && o.IndexOfAny([';', ',', ' ', '\r', '\n']) < 0)
            .Select(o => o.Trim())
            .ToArray();
        _denyFraming = origins.Length == 0;
        var frameAncestors = _denyFraming ? "'none'" : string.Join(' ', origins);

        // Security:AllowedScriptHashes: sha256/sha384/sha512 hashes for inline scripts that must be allowed
        // (e.g. OpenIddict form_post auto-submit). Values must be bare hashes without quotes; this method adds them.
        var scriptHashes = (configuration.GetSection("Security:AllowedScriptHashes").Get<string[]>() ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => $"'{h.Trim()}'")
            .ToArray();
        var scriptSrc = scriptHashes.Length > 0 ? $"'self' {string.Join(' ', scriptHashes)}" : "'self'";

        // style 'unsafe-inline': tenant CSS in LoginPage and React style attributes. No script unsafe-inline/eval.
        // img data:/https: for tenant logos. connect 'self' covers API + SignalR. form-action deliberately omitted (form_post).
        _csp = "default-src 'self'; " +
               $"script-src {scriptSrc}; " +
               "style-src 'self' 'unsafe-inline'; " +
               "img-src 'self' data: https:; " +
               "font-src 'self' data:; " +
               "connect-src 'self'; " +
               "object-src 'none'; " +
               "base-uri 'self'; " +
               $"frame-ancestors {frameAncestors}";
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["Content-Security-Policy"] = _csp;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            if (_denyFraming) h["X-Frame-Options"] = "DENY"; else h.Remove("X-Frame-Options");
            if (context.Request.IsHttps)
                h["Strict-Transport-Security"] = "max-age=31536000"; // no includeSubDomains/preload: shared host
            return Task.CompletedTask;
        });
        return _next(context);
    }
}
