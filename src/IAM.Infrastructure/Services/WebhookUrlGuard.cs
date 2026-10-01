using System.Net;
using System.Net.Sockets;
using IAM.Core.Security;
using IAM.Core.Services;
using Microsoft.Extensions.Configuration;

namespace IAM.Infrastructure.Services;

public class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
        Dns.GetHostAddressesAsync(host, ct);
}

public class WebhookUrlGuard : IWebhookUrlGuard
{
    public const string BlockedMessage = "Webhook URL must point to a public internet address (private, loopback and link-local targets are not allowed).";

    private readonly IHostResolver _resolver;
    private readonly HashSet<string> _allowedHosts;

    public WebhookUrlGuard(IHostResolver resolver, IConfiguration configuration)
    {
        _resolver = resolver;
        _allowedHosts = new HashSet<string>(
            configuration.GetSection("Webhooks:AllowedHosts").Get<string[]>() ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string?> CheckAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return "Webhook URL must be a valid absolute HTTP or HTTPS URL.";

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Webhook URL must not contain credentials.";

        try
        {
            await ResolveAllowedAsync(uri.IdnHost, ct);
            return null;
        }
        catch (WebhookTargetBlockedException ex)
        {
            return ex.Message;
        }
    }

    public async Task<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken ct = default)
    {
        host = host.Trim().TrimStart('[').TrimEnd(']');
        var allowlisted = _allowedHosts.Contains(host);

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = await _resolver.ResolveAsync(host, ct);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new WebhookTargetBlockedException("Webhook host could not be resolved.");
            }
        }

        if (addresses.Length == 0)
            throw new WebhookTargetBlockedException("Webhook host could not be resolved.");

        // Every address must be public: a host that ALSO resolves to an internal address is refused.
        if (!allowlisted && addresses.Any(a => !PublicAddress.IsPublic(a)))
            throw new WebhookTargetBlockedException(BlockedMessage);

        return addresses;
    }
}
