using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4703: every /api/securityalerts action needs SuperAdmin or SecurityAdmin, SIEM credentials are write-only,
/// webhook/Slack/SIEM targets must be public http(s) URLs (at save and at send time), and rules with an automatic
/// response can only be saved by a SuperAdmin. Outbound calls go to a recording stub (no network, fake DNS).
/// </summary>
public class SecurityAlertsAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private const string Secret = "siem-secret-token-123";
    private const string PublicUrl = "https://93.184.216.34/hook";

    private sealed class FakeResolver : IHostResolver
    {
        public Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
            Task.FromResult(host switch
            {
                "internal.example.test" => new[] { System.Net.IPAddress.Parse("10.1.2.3") },
                "localhost" => new[] { System.Net.IPAddress.Loopback },
                _ => new[] { System.Net.IPAddress.Parse("93.184.216.34") }
            });
    }

    private sealed class Recorder
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Recorder _recorder;
        public RecordingHandler(Recorder recorder) => _recorder = recorder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _recorder.Requests.Enqueue(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly Recorder _recorder;
        public StubFactory(Recorder recorder) => _recorder = recorder;
        public HttpClient CreateClient(string name) => new(new RecordingHandler(_recorder));
    }

    private readonly Recorder _recorder = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public SecurityAlertsAuthorizationTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IHostResolver>(new FakeResolver());
            s.AddSingleton<IHttpClientFactory>(new StubFactory(_recorder));
        }));
    }

    // ----- helpers ---------------------------------------------------------------------------

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(params string[] roles)
    {
        var id = Guid.NewGuid();
        return ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(id, $"{id:N}@alerts-authz.test", roles, null));
    }

    /// <summary>Shaped like DeviceAuthenticationService's token: sub = device GUID, tenant_id, token_type=device, no roles.</summary>
    private static string DeviceToken()
    {
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("DEVELOPMENT_SECRET_KEY_CHANGE_IN_PRODUCTION_32_CHARS_MIN"));
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "https://localhost:5001",
            audience: "iam-api",
            claims: new[]
            {
                new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim("device_id", "dev-1"),
                new System.Security.Claims.Claim("tenant_id", Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim("token_type", "device"),
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient OrdinaryUser() => ClientAs("User");
    private HttpClient SecurityAdmin() => ClientAs("SecurityAdmin");
    private HttpClient SuperAdmin() => ClientAs("SuperAdmin");

    private async Task<Guid> SeedRuleAsync(string? autoResponse = null, string channels = "[]")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var rule = new AlertRule
        {
            Id = Guid.NewGuid(), Name = "seeded", Condition = "{}", Channels = channels, CooldownMinutes = 0,
            AutoResponseAction = autoResponse, IsActive = true
        };
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private async Task<Guid> SeedSiemAsync(string endpoint = PublicUrl, string? authConfig = $"{{\"apiKey\":\"{Secret}\"}}")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var siem = new SiemIntegration
        {
            Id = Guid.NewGuid(), Name = "seeded-siem", Type = SiemType.Webhook, EndpointUrl = endpoint,
            AuthConfig = authConfig, Format = "JSON", IsActive = true
        };
        db.SiemIntegrations.Add(siem);
        await db.SaveChangesAsync();
        return siem.Id;
    }

    private async Task<SiemIntegration> SiemRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.SiemIntegrations.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private static object RuleBody(string? autoResponse = null, string? channels = null) => new
    {
        name = $"rule-{Guid.NewGuid():N}"[..14],
        severity = 1,
        channels,
        autoResponseAction = autoResponse
    };

    private static object SiemBody(string endpoint, string? authConfig = null) => new
    {
        name = $"siem-{Guid.NewGuid():N}"[..14],
        type = 1,
        endpointUrl = endpoint,
        authConfig
    };

    private static readonly string[] ActionNames =
    {
        "GET rules", "GET rules/{id}", "POST rules", "PUT rules/{id}", "DELETE rules/{id}", "GET alerts", "GET history",
        "POST acknowledge", "GET siem", "GET siem/{id}", "POST siem", "PUT siem/{id}", "DELETE siem/{id}"
    };

    /// <summary>One call per controller action (13), in the order of <see cref="ActionNames"/>.</summary>
    private async Task<HttpStatusCode[]> AllActionsAsync(HttpClient c)
    {
        var rule = await SeedRuleAsync();
        var siem = await SeedSiemAsync();
        return new[]
        {
            (await c.GetAsync("/api/securityalerts/rules")).StatusCode,
            (await c.GetAsync($"/api/securityalerts/rules/{rule}")).StatusCode,
            (await c.PostAsJsonAsync("/api/securityalerts/rules", RuleBody())).StatusCode,
            (await c.PutAsJsonAsync($"/api/securityalerts/rules/{rule}", RuleBody())).StatusCode,
            (await c.DeleteAsync($"/api/securityalerts/rules/{rule}")).StatusCode,
            (await c.GetAsync("/api/securityalerts")).StatusCode,
            (await c.GetAsync("/api/securityalerts/history")).StatusCode,
            (await c.PostAsync($"/api/securityalerts/{Guid.NewGuid()}/acknowledge", null)).StatusCode,
            (await c.GetAsync("/api/securityalerts/siem")).StatusCode,
            (await c.GetAsync($"/api/securityalerts/siem/{siem}")).StatusCode,
            (await c.PostAsJsonAsync("/api/securityalerts/siem", SiemBody(PublicUrl))).StatusCode,
            (await c.PutAsJsonAsync($"/api/securityalerts/siem/{siem}", SiemBody(PublicUrl))).StatusCode,
            (await c.DeleteAsync($"/api/securityalerts/siem/{siem}")).StatusCode,
        };
    }

    private static void AssertAll(HttpStatusCode[] statuses, HttpStatusCode expected)
    {
        for (var i = 0; i < statuses.Length; i++)
            Assert.True(expected == statuses[i], $"{ActionNames[i]} returned {statuses[i]}, expected {expected}");
    }

    // ----- who may use the API ---------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_Gets401_OnEveryAction() =>
        AssertAll(await AllActionsAsync(_factory.CreateClient()), HttpStatusCode.Unauthorized);

    [Fact]
    public async Task OrdinaryUser_Gets403_OnEveryAction_AndNothingChanges()
    {
        var rule = await SeedRuleAsync();
        var siem = await SeedSiemAsync();
        var c = OrdinaryUser();
        int SiemCount() { using var s = _factory.Services.CreateScope(); return s.ServiceProvider.GetRequiredService<IAMDbContext>().SiemIntegrations.Count(); }
        int RuleCount() { using var s = _factory.Services.CreateScope(); return s.ServiceProvider.GetRequiredService<IAMDbContext>().AlertRules.Count(); }
        var (siemsBefore, rulesBefore) = (SiemCount(), RuleCount());

        AssertAll(await AllActionsAsync(c), HttpStatusCode.Forbidden);

        // AllActionsAsync seeds one rule and one SIEM connection itself; nothing else may be created or removed.
        Assert.Equal(siemsBefore + 1, SiemCount());
        Assert.Equal(rulesBefore + 1, RuleCount());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.True(await db.AlertRules.AnyAsync(r => r.Id == rule));
        Assert.True(await db.SiemIntegrations.AnyAsync(s => s.Id == siem));
    }

    [Fact]
    public async Task TenantAdminRoles_AreNotSecurityAdmins()
    {
        foreach (var role in new[] { "TenantAdmin", "BuildingOwner", "BuildingManager", "SystemAdmin" })
            AssertAll(await AllActionsAsync(ClientAs(role)), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeviceToken_Gets403_OnEveryAction()
    {
        AssertAll(await AllActionsAsync(ClientWithToken(DeviceToken())), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceAccountToken_Gets403_OnEveryAction()
    {
        var token = TestAuthenticationHelper.GenerateServiceAccountToken("svc", "alerts:read", "alerts:write");
        AssertAll(await AllActionsAsync(ClientWithToken(token)), HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("SecurityAdmin")]
    [InlineData("SuperAdmin")]
    public async Task SecurityAdminAndSuperAdmin_CanUseEveryAction(string role)
    {
        var statuses = await AllActionsAsync(ClientAs(role));

        var expected = new[]
        {
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.NoContent,
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NotFound, // unknown alert id: authorized, then not found
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.NoContent,
        };
        for (var i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == statuses[i], $"{ActionNames[i]} returned {statuses[i]}, expected {expected[i]}");
    }

    // ----- SIEM credentials are write-only -----------------------------------------------------------

    [Fact]
    public async Task SiemReads_NeverContainTheCredential()
    {
        var id = await SeedSiemAsync();
        var c = SecurityAdmin();

        var list = await (await c.GetAsync("/api/securityalerts/siem")).Content.ReadAsStringAsync();
        var one = await (await c.GetAsync($"/api/securityalerts/siem/{id}")).Content.ReadAsStringAsync();

        foreach (var body in new[] { list, one })
        {
            Assert.DoesNotContain(Secret, body);
            Assert.DoesNotContain("\"authConfig\"", body, StringComparison.OrdinalIgnoreCase);
        }

        var json = JsonSerializer.Deserialize<JsonElement>(one);
        Assert.True(json.GetProperty("hasAuthConfig").GetBoolean());
        Assert.Equal("********", json.GetProperty("authConfigMasked").GetString());
    }

    [Fact]
    public async Task SiemCreateAndUpdateResponses_NeverContainTheCredential()
    {
        var c = SecurityAdmin();
        var created = await c.PostAsJsonAsync("/api/securityalerts/siem", SiemBody(PublicUrl, $"{{\"apiKey\":\"{Secret}\"}}"));
        var createdBody = await created.Content.ReadAsStringAsync();
        var id = JsonSerializer.Deserialize<JsonElement>(createdBody).GetProperty("id").GetGuid();

        var updated = await c.PutAsJsonAsync($"/api/securityalerts/siem/{id}", SiemBody(PublicUrl, $"{{\"apiKey\":\"{Secret}2\"}}"));
        var updatedBody = await updated.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.DoesNotContain(Secret, createdBody);
        Assert.DoesNotContain(Secret, updatedBody);
        Assert.Equal($"{{\"apiKey\":\"{Secret}2\"}}", (await SiemRowAsync(id)).AuthConfig);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("********")]
    public async Task SiemUpdate_WithoutNewCredential_KeepsTheStoredOne(string? authConfig)
    {
        var id = await SeedSiemAsync();

        var response = await SecurityAdmin().PutAsJsonAsync($"/api/securityalerts/siem/{id}", SiemBody(PublicUrl, authConfig));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{{\"apiKey\":\"{Secret}\"}}", (await SiemRowAsync(id)).AuthConfig);
    }

    // ----- target URLs ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:8080/x")]
    [InlineData("http://localhost/x")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("https://internal.example.test/hook")]
    [InlineData("ftp://93.184.216.34/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task SiemEndpoint_ThatIsNotAPublicHttpUrl_IsRejected_OnCreateAndUpdate(string url)
    {
        var c = SecurityAdmin();
        var id = await SeedSiemAsync();

        var create = await c.PostAsJsonAsync("/api/securityalerts/siem", SiemBody(url));
        var update = await c.PutAsJsonAsync($"/api/securityalerts/siem/{id}", SiemBody(url));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal(PublicUrl, (await SiemRowAsync(id)).EndpointUrl);
    }

    [Theory]
    [InlineData("webhook", "http://169.254.169.254/x")]
    [InlineData("slack", "https://10.0.0.5/services/T/B/x")]
    [InlineData("webhook", "https://internal.example.test/hook")]
    public async Task RuleChannel_WithInternalTarget_IsRejected_OnCreateAndUpdate(string type, string target)
    {
        var c = SuperAdmin();
        var rule = await SeedRuleAsync();
        var channels = JsonSerializer.Serialize(new[] { new { type, target } });

        var create = await c.PostAsJsonAsync("/api/securityalerts/rules", RuleBody(channels: channels));
        var update = await c.PutAsJsonAsync($"/api/securityalerts/rules/{rule}", RuleBody(channels: channels));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task RuleChannels_WithPublicTargetsAndEmail_AreAccepted_AndMalformedJsonIsRejected()
    {
        var c = SecurityAdmin();
        var ok = JsonSerializer.Serialize(new object[]
        {
            new { type = "webhook", target = PublicUrl },
            new { type = "slack", target = "https://hooks.example.test/services/T/B/x" },
            new { type = "email", target = "soc@example.test" },
        });

        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/securityalerts/rules", RuleBody(channels: ok))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/securityalerts/rules", RuleBody(channels: "{not json"))).StatusCode);
    }

    // ----- automatic responses are SuperAdmin only -----------------------------------------------------

    [Theory]
    [InlineData("lock_account")]
    [InlineData("kill_sessions")]
    [InlineData("force_mfa")]
    public async Task AutoResponseRule_CanOnlyBeSavedBySuperAdmin(string action)
    {
        var existing = await SeedRuleAsync();
        var sec = SecurityAdmin();

        var create = await sec.PostAsJsonAsync("/api/securityalerts/rules", RuleBody(action));
        var update = await sec.PutAsJsonAsync($"/api/securityalerts/rules/{existing}", RuleBody(action));

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            Assert.Null((await db.AlertRules.AsNoTracking().FirstAsync(r => r.Id == existing)).AutoResponseAction);
        }

        var su = SuperAdmin();
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/securityalerts/rules", RuleBody(action))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await su.PutAsJsonAsync($"/api/securityalerts/rules/{existing}", RuleBody(action))).StatusCode);
    }

    [Fact]
    public async Task SecurityAdmin_CanSaveRulesWithoutAutoResponse()
    {
        Assert.Equal(HttpStatusCode.Created, (await SecurityAdmin().PostAsJsonAsync("/api/securityalerts/rules", RuleBody(autoResponse: ""))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SecurityAdmin().PostAsJsonAsync("/api/securityalerts/rules", RuleBody())).StatusCode);
    }

    // ----- sending: stored credentials keep working, internal targets are skipped ------------------------

    [Fact]
    public async Task SiemExport_UsesTheStoredCredential_AndSkipsInternalTargets()
    {
        _recorder.Requests.Clear();
        await SeedSiemAsync(PublicUrl);
        await SeedSiemAsync("http://169.254.169.254/latest/meta-data/"); // saved before the guard existed
        await SeedSiemAsync("https://internal.example.test/hook");       // resolves to a private address

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecurityAlertService>();
        await service.ExportToSiemAsync("user.login", new { ok = true });

        var sent = _recorder.Requests.Where(r => r.RequestUri!.Host != "93.184.216.34").ToList();
        Assert.Empty(sent.Where(r => r.RequestUri!.Host is "169.254.169.254" or "internal.example.test"));
        var toPublic = _recorder.Requests.Where(r => r.RequestUri!.Host == "93.184.216.34").ToList();
        Assert.NotEmpty(toPublic);
    }

    [Fact]
    public async Task AlertWebhookChannel_ToAnInternalTarget_IsSkipped_AndAPublicOneIsSent()
    {
        _recorder.Requests.Clear();
        var internalRule = await SeedRuleAsync(channels: "[{\"type\":\"webhook\",\"target\":\"http://10.0.0.5/hook\"},{\"type\":\"slack\",\"target\":\"http://127.0.0.1/slack\"}]");
        var publicRule = await SeedRuleAsync(channels: "[{\"type\":\"webhook\",\"target\":\"" + PublicUrl + "\"}]");

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecurityAlertService>();
        await service.FireAlertAsync(internalRule, "internal", null);
        await service.FireAlertAsync(publicRule, "public", null);

        // Notifications are fire-and-forget: wait for the public one, then check the internal ones never went out.
        for (var i = 0; i < 50 && !_recorder.Requests.Any(r => r.RequestUri!.Host == "93.184.216.34"); i++)
            await Task.Delay(100);
        await Task.Delay(300);

        Assert.Contains(_recorder.Requests, r => r.RequestUri!.Host == "93.184.216.34");
        Assert.DoesNotContain(_recorder.Requests, r => r.RequestUri!.Host is "10.0.0.5" or "127.0.0.1");
    }
}
