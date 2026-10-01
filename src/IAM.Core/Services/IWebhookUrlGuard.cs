using System.Net;

namespace IAM.Core.Services;

/// <summary>Resolves a host name to IP addresses. A seam so tests can fake DNS.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default);
}

/// <summary>
/// SSRF guard for webhook targets (task 4702): a URL is acceptable only when it is http(s), carries no
/// credentials, and its host is - or resolves ONLY to - public addresses. Hosts on the optional
/// <c>Webhooks:AllowedHosts</c> allowlist (local development) skip the address check.
/// </summary>
public interface IWebhookUrlGuard
{
    /// <summary>Returns an error message when the URL must not be called, or null when it is allowed.</summary>
    Task<string?> CheckAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Resolves <paramref name="host"/> and returns the addresses it may be connected to, or throws
    /// <see cref="WebhookTargetBlockedException"/>. Used at connection time so the address that is
    /// checked is the address that is dialled (no DNS rebinding window).
    /// </summary>
    Task<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken ct = default);
}

public class WebhookTargetBlockedException : Exception
{
    public WebhookTargetBlockedException(string message) : base(message)
    {
    }
}
