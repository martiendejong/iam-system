using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using IAM.EdgeGateway;
using IAM.Infrastructure.Services;
using IAM.SDK.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IAM.EdgeGateway.Tests;

public class EdgeGatewayTests
{
    private const string Device = "dev-1";

    public static IEnumerable<object[]> Pairs => new List<object[]>
    {
        new object[] { "acme:hq:floor-1:*", "acme:hq:floor-1:hvac:read" },
        new object[] { "acme:hq:floor-1:*", "acme:hq:floor-1:read" },
        new object[] { "acme:hq:floor-1:*", "acme:hq:floor-10:read" },
        new object[] { "acme:hq:floor-1:*", "acme:hq:floor-10:hvac:read" },
        new object[] { "acme:hq:floor-1:*", "acme:hq:floor-1x:hvac:read" },
        new object[] { "acme:hq:floor-1:*", "acme:hq:floor-2:read" },
        new object[] { "acme:hq:*", "acme:hq:floor-3:hvac:unit-247:telemetry:write" },
        new object[] { "acme:hq:floor-1:hvac:read", "acme:hq:floor-1:hvac:read" },
        new object[] { "acme:hq:floor-1:hvac:read", "acme:hq:floor-1:hvac:write" },
        new object[] { "acme:hq:floor-1", "acme:hq:floor-1:hvac:read" },
        new object[] { "acme:hq:floor-1", "acme:hq:floor-10:hvac:read" },
        new object[] { "acme:hq:floor-1:*", "ACME:HQ:FLOOR-1:HVAC:READ" },
        new object[] { "*", "acme:hq:floor-1:read" },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Edge_matcher_equals_cloud_matcher(string permission, string requested)
    {
        var cloud = typeof(DeviceAuthenticationService).GetMethod(
            "IsHierarchicalMatch", BindingFlags.NonPublic | BindingFlags.Static)!;
        var expected = (bool)cloud.Invoke(null, new object[] { permission, requested })!;

        Assert.Equal(expected, EdgeGatewayService.IsHierarchicalMatch(permission, requested));
    }

    [Theory]
    [InlineData("acme:hq:floor-1", "hvac:read", true)]
    [InlineData("acme:hq:floor-1:hvac", "read", true)]
    [InlineData("acme:hq:floor-10", "read", false)]
    [InlineData("acme:hq:floor-1x:hvac", "read", false)]
    public void Wildcard_covers_own_resource_and_children_only(string resource, string action, bool expected)
    {
        var claims = new DeviceClaimsData { Permissions = { "acme:hq:floor-1:*" } };
        Assert.Equal(expected, EdgeGatewayService.EvaluateLocalAuthorization(claims, resource, action));
    }

    [Fact]
    public async Task Cloud_server_error_denies_and_ignores_cached_claims()
    {
        var (svc, cache) = await Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        cache.CacheDeviceClaims(Device, new DeviceClaimsData { Permissions = { "acme:*" } });

        var r = await svc.AuthorizeDeviceAsync(Device, "acme:x", "read");

        Assert.False(r.Allowed);
        Assert.False(r.FromCache);
        Assert.True(svc.IsOnline);
    }

    [Fact]
    public async Task Cloud_forbidden_denies()
    {
        var (svc, cache) = await Build(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        cache.CacheDeviceClaims(Device, new DeviceClaimsData { Permissions = { "acme:*" } });

        Assert.False((await svc.AuthorizeDeviceAsync(Device, "acme:x", "read")).Allowed);
    }

    [Fact]
    public async Task Connectivity_failure_falls_back_to_cached_claims()
    {
        var (svc, cache) = await Build(_ => throw new HttpRequestException("connection refused"));
        cache.CacheDeviceClaims(Device, new DeviceClaimsData { Permissions = { "acme:hq:floor-1:*" } });

        var ok = await svc.AuthorizeDeviceAsync(Device, "acme:hq:floor-1", "read");
        var no = await svc.AuthorizeDeviceAsync(Device, "acme:hq:floor-10", "read");

        Assert.True(ok.Allowed);
        Assert.True(ok.OfflineMode);
        Assert.False(no.Allowed);
        Assert.False(svc.IsOnline);
    }

    [Fact]
    public void IsConnectivityException_distinguishes_http_status_from_network_failure()
    {
        Assert.True(EdgeGatewayService.IsConnectivityException(new HttpRequestException("down")));
        Assert.True(EdgeGatewayService.IsConnectivityException(new TaskCanceledException()));
        Assert.False(EdgeGatewayService.IsConnectivityException(
            new HttpRequestException("500", null, HttpStatusCode.InternalServerError)));
        Assert.False(EdgeGatewayService.IsConnectivityException(new InvalidOperationException()));
    }

    private static async Task<(EdgeGatewayService, EdgeAuthorizationCache)> Build(
        Func<HttpRequestMessage, HttpResponseMessage> authorizeBehaviour)
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/hmac"))
                return Json(new { success = true, accessToken = "t", expiresIn = 3600, deviceId = Device });
            return authorizeBehaviour(req);
        });
        var http = new HttpClient(handler);
        var opts = new IamDeviceClientOptions { BaseUrl = "http://localhost", DeviceId = Device };
        var client = new IamDeviceClient(http, Options.Create(opts));
        await client.AuthenticateWithHmacAsync("secret");

        var cache = new EdgeAuthorizationCache();
        var svc = new EdgeGatewayService(client, new HttpClient(handler) { BaseAddress = new Uri("http://localhost") },
            cache, NullLogger<EdgeGatewayService>.Instance, new EdgeGatewayOptions());
        return (svc, cache);
    }

    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(f(r));
    }
}
