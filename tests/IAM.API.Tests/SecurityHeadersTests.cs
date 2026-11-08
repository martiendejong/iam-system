using IAM.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IAM.API.Tests;

/// <summary>Task 5579: every response must carry the fixed security header set.</summary>
public class SecurityHeadersTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;
    public SecurityHeadersTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/api/health")]
    [InlineData("/login")]
    public async Task Responses_CarryAllSecurityHeaders(string path)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var r = await client.GetAsync(path);

        var csp = Assert.Single(r.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        var script = csp.Split(';').Select(x => x.Trim()).First(x => x.StartsWith("script-src"));
        Assert.DoesNotContain("unsafe-inline", script);
        Assert.Equal("nosniff", Assert.Single(r.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(r.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(r.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public async Task Hsts_OnlyOverHttps()
    {
        var http = await _factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") }).GetAsync("/api/does-not-exist");
        Assert.False(http.Headers.Contains("Strict-Transport-Security"));
        var https = await _factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") }).GetAsync("/api/does-not-exist");
        var hsts = Assert.Single(https.Headers.GetValues("Strict-Transport-Security"));
        Assert.DoesNotContain("includeSubDomains", hsts);
        Assert.DoesNotContain("preload", hsts);
    }

    [Fact]
    public async Task ConfiguredFrameAncestors_ReplaceDenyAndDropXFrameOptions()
    {
        var client = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Security:AllowedFrameAncestors:0"] = "https://partner.example" })))
            .CreateClient(new() { AllowAutoRedirect = false });
        var r = await client.GetAsync("/api/does-not-exist");
        Assert.Contains("frame-ancestors https://partner.example", Assert.Single(r.Headers.GetValues("Content-Security-Policy")));
        Assert.False(r.Headers.Contains("X-Frame-Options"));
    }
}
