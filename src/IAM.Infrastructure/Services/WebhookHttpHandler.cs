using System.Net;
using System.Net.Sockets;
using IAM.Core.Services;

namespace IAM.Infrastructure.Services;

/// <summary>
/// The primary handler of the "WebhookDelivery" HttpClient (task 4702). The connect callback resolves the
/// host itself, refuses non-public addresses and dials exactly the address it checked, so a DNS answer that
/// changes between the save-time check and the connection (DNS rebinding) cannot reach an internal host.
/// Redirects are not followed and system proxies are not used (a proxy would be dialled instead of the target).
/// </summary>
public static class WebhookHttpHandler
{
    public static SocketsHttpHandler Create(IWebhookUrlGuard guard, bool acceptAnyServerCertificate = false)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await guard.ResolveAllowedAsync(context.DnsEndPoint.Host, ct);

                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException ex)
                    {
                        socket.Dispose();
                        last = ex;
                    }
                }

                throw new HttpRequestException("Could not connect to the webhook target.", last);
            }
        };

        if (acceptAnyServerCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        return handler;
    }
}
