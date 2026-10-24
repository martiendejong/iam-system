using System.Net;
using System.Net.Sockets;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4702: webhook targets must be public addresses - checked when saving, again at every delivery
/// (fake resolver + stub handler), and once more at connection time (real sockets on loopback).
/// </summary>
public class WebhookSsrfGuardTests
{
    private static readonly Guid TenantId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private sealed class FakeResolver : IHostResolver
    {
        public Dictionary<string, IPAddress[]> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Lookups { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
        {
            Lookups++;
            return Answers.TryGetValue(host, out var a)
                ? Task.FromResult(a)
                : throw new SocketException((int)SocketError.HostNotFound);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "secret-internal-body";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static WebhookUrlGuard NewGuard(FakeResolver resolver, params string[] allowedHosts) =>
        new(resolver, new ConfigurationBuilder().AddInMemoryCollection(
            allowedHosts.Select((h, i) => new KeyValuePair<string, string?>($"Webhooks:AllowedHosts:{i}", h))).Build());

    private static IAMDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FakeResolver ResolverWithPublicHost()
    {
        var r = new FakeResolver();
        r.Answers["hooks.example.com"] = new[] { IPAddress.Parse("93.184.216.34") };
        return r;
    }

    // ----- save-time guard ---------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://localhost:5000/hook")]
    [InlineData("http://10.1.2.3/hook")]
    [InlineData("https://192.168.0.5/hook")]
    [InlineData("http://172.20.0.1/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://[::ffff:127.0.0.1]/hook")]
    [InlineData("http://[::ffff:169.254.169.254]/hook")]
    [InlineData("http://[fe80::1]/hook")]
    [InlineData("http://[fd00:ec2::254]/hook")]
    [InlineData("http://2130706433/hook")]       // decimal form of 127.0.0.1
    [InlineData("http://0x7f000001/hook")]       // hex form
    [InlineData("http://127.1/hook")]            // short form
    [InlineData("http://internal.corp/hook")]    // resolves to private below
    [InlineData("http://mixed.example.com/hook")] // resolves to public AND private
    public async Task Check_RejectsBlockedTargets(string url)
    {
        var resolver = ResolverWithPublicHost();
        resolver.Answers["localhost"] = new[] { IPAddress.Loopback, IPAddress.IPv6Loopback };
        resolver.Answers["internal.corp"] = new[] { IPAddress.Parse("10.0.0.7") };
        resolver.Answers["mixed.example.com"] = new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.7") };

        Assert.NotNull(await NewGuard(resolver).CheckAsync(url));
    }

    [Theory]
    [InlineData("ftp://hooks.example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://hooks.example.com/")]
    [InlineData("not a url")]
    [InlineData("//hooks.example.com/x")]
    [InlineData("https://user:pass@hooks.example.com/x")]
    public async Task Check_RejectsNonHttpOrMalformedOrCredentialUrls(string url)
    {
        Assert.NotNull(await NewGuard(ResolverWithPublicHost()).CheckAsync(url));
    }

    [Theory]
    [InlineData("https://hooks.example.com/x")]
    [InlineData("http://hooks.example.com:8080/x?y=1")]
    [InlineData("https://93.184.216.34/x")]
    [InlineData("https://[2606:4700:4700::1111]/x")]
    public async Task Check_AllowsPublicTargets(string url)
    {
        Assert.Null(await NewGuard(ResolverWithPublicHost()).CheckAsync(url));
    }

    [Fact]
    public async Task Check_RejectsUnresolvableHost()
    {
        Assert.NotNull(await NewGuard(new FakeResolver()).CheckAsync("https://nope.invalid/x"));
    }

    [Fact]
    public async Task Check_AllowlistedHost_SkipsTheAddressCheck_OthersStillBlocked()
    {
        var resolver = new FakeResolver();
        resolver.Answers["dev-receiver.local"] = new[] { IPAddress.Loopback };
        resolver.Answers["other.local"] = new[] { IPAddress.Loopback };
        var guard = NewGuard(resolver, "dev-receiver.local");

        Assert.Null(await guard.CheckAsync("http://dev-receiver.local:9000/x"));
        Assert.NotNull(await guard.CheckAsync("http://other.local:9000/x"));
    }

    [Fact]
    public async Task WebhookService_CreateAndUpdate_RejectBlockedUrls_AndStoreNothing()
    {
        using var context = NewContext();
        var service = new WebhookService(context, new NoopBus(), NullLogger<WebhookService>.Instance, NewGuard(ResolverWithPublicHost()));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateSubscriptionAsync(NewSubscription("http://169.254.169.254/")));
        Assert.Empty(context.Set<WebhookSubscription>());

        var ok = await service.CreateSubscriptionAsync(NewSubscription("https://hooks.example.com/x"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateSubscriptionAsync(ok.Id, null, "http://127.0.0.1/", null, null));
        Assert.Equal("https://hooks.example.com/x", (await context.Set<WebhookSubscription>().AsNoTracking().SingleAsync()).Url);
    }

    private sealed class NoopBus : IEventBus
    {
        public Task PublishAsync(string eventType, object payload, Guid? tenantId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<WebhookDelivery>> GetDeliveryHistoryAsync(Guid subscriptionId, int limit = 50, CancellationToken ct = default) => Task.FromResult(new List<WebhookDelivery>());
    }

    private static WebhookSubscription NewSubscription(string url, int maxRetries = 3) => new()
    {
        Name = "hook",
        Url = url,
        TenantId = TenantId,
        Events = "[\"*\"]",
        IsActive = true,
        ContentType = "application/json",
        MaxRetries = maxRetries,
        TimeoutSeconds = 5
    };

    // ----- delivery-time guard -----------------------------------------------------------------

    private static async Task<(List<WebhookDelivery> Deliveries, StubHandler Handler)> PublishAsync(
        IAMDbContext context, FakeResolver resolver, string url, int maxRetries = 3)
    {
        var handler = new StubHandler();
        var bus = new EventBusService(context, new StubFactory(handler), NullLogger<EventBusService>.Instance, NewGuard(resolver));
        context.Set<WebhookSubscription>().Add(NewSubscription(url, maxRetries));
        await context.SaveChangesAsync();

        await bus.PublishAsync("user.created", new { id = 1 }, TenantId);

        return (await context.Set<WebhookDelivery>().AsNoTracking().ToListAsync(), handler);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:5000/admin")]
    [InlineData("http://internal.corp/x")]
    public async Task Delivery_ToBlockedTarget_IsRecordedFailed_WithoutBody_NoRequestSent_NoRetries(string url)
    {
        using var context = NewContext();
        var resolver = ResolverWithPublicHost();
        resolver.Answers["internal.corp"] = new[] { IPAddress.Parse("10.0.0.7") };

        // An old row saved before the guard existed: MaxRetries 3, yet exactly one failed attempt is recorded.
        var (deliveries, handler) = await PublishAsync(context, resolver, url, maxRetries: 3);

        var delivery = Assert.Single(deliveries);
        Assert.False(delivery.Success);
        Assert.Null(delivery.ResponseBody);
        Assert.Equal(0, delivery.HttpStatusCode);
        Assert.Equal(EventBusService.BlockedDeliveryError, delivery.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Delivery_ReResolvesTheHost_DnsRebindingToAnInternalAddressIsRefused()
    {
        using var context = NewContext();
        var resolver = ResolverWithPublicHost();
        var handler = new StubHandler();
        var bus = new EventBusService(context, new StubFactory(handler), NullLogger<EventBusService>.Instance, NewGuard(resolver));
        context.Set<WebhookSubscription>().Add(NewSubscription("https://hooks.example.com/x"));
        await context.SaveChangesAsync();

        await bus.PublishAsync("user.created", new { }, TenantId);
        Assert.Equal(1, handler.Calls); // public: delivered

        resolver.Answers["hooks.example.com"] = new[] { IPAddress.Parse("169.254.169.254") }; // attacker flips the record
        await bus.PublishAsync("user.created", new { }, TenantId);

        Assert.Equal(1, handler.Calls); // second delivery never left
        var last = (await context.Set<WebhookDelivery>().AsNoTracking().ToListAsync()).OrderBy(d => d.CreatedAt).Last();
        Assert.False(last.Success);
        Assert.Null(last.ResponseBody);
    }

    [Fact]
    public async Task Delivery_ToPublicHost_Succeeds_AndKeepsTheResponse()
    {
        using var context = NewContext();

        var (deliveries, handler) = await PublishAsync(context, ResolverWithPublicHost(), "https://hooks.example.com/x");

        var delivery = Assert.Single(deliveries);
        Assert.True(delivery.Success);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("secret-internal-body", delivery.ResponseBody);
    }

    // ----- connection-time guard (real sockets) ------------------------------------------------

    [Fact]
    public void Handler_DoesNotFollowRedirects_OrUseAProxy()
    {
        using var handler = WebhookHttpHandler.Create(NewGuard(new FakeResolver()));

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task Handler_RefusesToConnectToLoopback_ListenerNeverSeesAConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new HttpClient(WebhookHttpHandler.Create(NewGuard(new FakeResolver())));

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/"));

            Assert.IsType<WebhookTargetBlockedException>(ex.GetBaseException());
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Handler_ResolvesAtConnectTime_AHostThatNowPointsInternalIsRefused()
    {
        var resolver = new FakeResolver();
        resolver.Answers["rebind.example.com"] = new[] { IPAddress.Loopback };
        using var client = new HttpClient(WebhookHttpHandler.Create(NewGuard(resolver)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://rebind.example.com:9/"));

        Assert.IsType<WebhookTargetBlockedException>(ex.GetBaseException());
    }

    [Fact]
    public async Task Handler_AllowlistedLoopbackHost_Connects_AndRedirectsAreNotFollowed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = 0;
        var server = Task.Run(async () =>
        {
            while (true)
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                requests++;
                var stream = tcp.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer);
                var response = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/next\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });

        try
        {
            using var client = new HttpClient(WebhookHttpHandler.Create(NewGuard(new FakeResolver(), "127.0.0.1")));

            using var response = await client.GetAsync($"http://127.0.0.1:{port}/");

            Assert.Equal(HttpStatusCode.Found, response.StatusCode); // returned as is, not followed
            Assert.Equal(1, requests);
        }
        finally
        {
            listener.Stop();
            try { await server; } catch { /* listener stopped */ }
        }
    }
}
