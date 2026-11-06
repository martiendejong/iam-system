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
/// Task 5153: the region health check (on demand and from the background worker, both RegionService) dials through the
/// webhook connection guard: private, loopback and link-local addresses are never connected to, and a redirect is
/// never followed. Real sockets on loopback: the guard is configured to allow only the named "public" host, which a
/// fake resolver maps to loopback, so the "private" listener is a real server that must stay untouched.
/// </summary>
public class RegionHealthClientTests
{
    private sealed class FakeResolver : IHostResolver
    {
        public Dictionary<string, IPAddress[]> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
            Answers.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new SocketException((int)SocketError.HostNotFound);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>A tiny HTTP server on loopback that counts connections and answers every request with the given reply.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private int _connections;
        private readonly string _reply;

        public Listener(string reply)
        {
            _reply = reply;
            _tcp.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_tcp.LocalEndpoint).Port;
        public int Connections => Volatile.Read(ref _connections);

        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _tcp.AcceptTcpClientAsync(_cts.Token);
                    Interlocked.Increment(ref _connections);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            _ = await stream.ReadAsync(buffer, _cts.Token);
                            var bytes = Encoding.ASCII.GetBytes(_reply);
                            await stream.WriteAsync(bytes, _cts.Token);
                        }
                    });
                }
            }
            catch (Exception)
            {
                // stopped
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _tcp.Stop();
        }
    }

    private static string Ok() => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";

    private static string Redirect(string location) =>
        $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private static RegionService NewService(IAMDbContext db, FakeResolver resolver, params string[] allowedHosts)
    {
        var guard = new WebhookUrlGuard(resolver, new ConfigurationBuilder().AddInMemoryCollection(
            allowedHosts.Select((h, i) => new KeyValuePair<string, string?>($"Webhooks:AllowedHosts:{i}", h))).Build());
        // The same construction Program.cs uses for the "RegionHealth" client.
        var client = new HttpClient(WebhookHttpHandler.Create(guard)) { Timeout = TimeSpan.FromSeconds(5) };
        return new RegionService(db, new SingleClientFactory(client), NullLogger<RegionService>.Instance);
    }

    private static IAMDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<RegionConfig> AddRegionAsync(IAMDbContext db, string endpoint)
    {
        var region = new RegionConfig { Name = $"r-{Guid.NewGuid():N}", Endpoint = endpoint, Status = RegionStatus.Active };
        db.RegionConfigs.Add(region);
        await db.SaveChangesAsync();
        return region;
    }

    [Fact]
    public async Task HealthCheck_NeverConnectsToALoopbackEndpoint()
    {
        using var server = new Listener(Ok());
        using var db = NewContext();
        var region = await AddRegionAsync(db, $"http://127.0.0.1:{server.Port}");

        var result = await NewService(db, new FakeResolver()).CheckRegionHealthAsync(region.Id);

        Assert.False(result.IsHealthy);
        Assert.Equal(RegionStatus.Offline, result.Status);
        Assert.Equal(-1, result.LatencyMs);
        Assert.Equal(0, server.Connections);
        Assert.Equal(RegionStatus.Offline, (await db.RegionConfigs.FindAsync(region.Id))!.Status);
    }

    [Fact]
    public async Task HealthCheck_NeverConnectsToAHostNameThatResolvesToAPrivateAddress()
    {
        using var server = new Listener(Ok());
        using var db = NewContext();
        var resolver = new FakeResolver();
        resolver.Answers["region.internal"] = new[] { IPAddress.Loopback };
        var region = await AddRegionAsync(db, $"http://region.internal:{server.Port}");

        var result = await NewService(db, resolver).CheckRegionHealthAsync(region.Id);

        Assert.False(result.IsHealthy);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task HealthCheck_DoesNotFollowARedirectToAPrivateAddress()
    {
        using var privateTarget = new Listener(Ok());
        using var redirector = new Listener(Redirect($"http://127.0.0.1:{privateTarget.Port}/health"));
        using var db = NewContext();
        // The redirector's host is allowlisted (so the first hop is dialled); the private target is not.
        var resolver = new FakeResolver();
        resolver.Answers["region.example.com"] = new[] { IPAddress.Loopback };
        var region = await AddRegionAsync(db, $"http://region.example.com:{redirector.Port}");

        var result = await NewService(db, resolver, "region.example.com").CheckRegionHealthAsync(region.Id);

        Assert.Equal(1, redirector.Connections);
        Assert.Equal(0, privateTarget.Connections);
        Assert.False(result.IsHealthy);
        Assert.Equal(RegionStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task HealthCheck_DoesNotFollowARedirectToTheMetadataAddress()
    {
        using var redirector = new Listener(Redirect("http://169.254.169.254/latest/meta-data/"));
        using var db = NewContext();
        var resolver = new FakeResolver();
        resolver.Answers["region.example.com"] = new[] { IPAddress.Loopback };
        var region = await AddRegionAsync(db, $"http://region.example.com:{redirector.Port}");

        var result = await NewService(db, resolver, "region.example.com").CheckRegionHealthAsync(region.Id);

        Assert.Equal(1, redirector.Connections);
        Assert.False(result.IsHealthy);
        Assert.Null(result.Error);   // a redirect is an unhealthy answer, not a followed request that failed
    }

    [Fact]
    public async Task HealthCheck_StillWorksForAnAllowedEndpoint()
    {
        using var server = new Listener(Ok());
        using var db = NewContext();
        var resolver = new FakeResolver();
        resolver.Answers["region.example.com"] = new[] { IPAddress.Loopback };
        var region = await AddRegionAsync(db, $"http://region.example.com:{server.Port}");

        var result = await NewService(db, resolver, "region.example.com").CheckRegionHealthAsync(region.Id);

        Assert.True(result.IsHealthy);
        Assert.Equal(RegionStatus.Active, result.Status);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task BackgroundWorkerPath_RunAllHealthChecks_SkipsPrivateEndpoints()
    {
        using var privateServer = new Listener(Ok());
        using var db = NewContext();
        await AddRegionAsync(db, $"http://127.0.0.1:{privateServer.Port}");
        await AddRegionAsync(db, "http://169.254.169.254");
        await AddRegionAsync(db, "http://10.0.0.5");

        await NewService(db, new FakeResolver()).RunAllHealthChecksAsync();

        Assert.Equal(0, privateServer.Connections);
        Assert.All(await db.RegionConfigs.ToListAsync(), r => Assert.Equal(RegionStatus.Offline, r.Status));
    }
}
