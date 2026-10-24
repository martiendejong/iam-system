using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Hazina.Security.ApiKeys;
using IAM.API.Controllers;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4705: risk thresholds and the dashboard are SuperAdmin/SecurityAdmin only, thresholds are validated so they
/// cannot switch protection off, and scores, trusted devices and manual assessments only work for the caller's own
/// user id (from the token) unless the caller is a platform security admin.
/// </summary>
public class RiskAssessmentAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public RiskAssessmentAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    // ----- helpers ---------------------------------------------------------------------------

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientAs(Guid userId, params string[] roles) =>
        ClientWithToken(TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@risk-authz.test", roles, null));

    private HttpClient SecurityAdmin() => ClientAs(Guid.NewGuid(), "SecurityAdmin");
    private HttpClient SuperAdmin() => ClientAs(Guid.NewGuid(), "SuperAdmin");

    /// <summary>Shaped like DeviceAuthenticationService's token: sub = device GUID, tenant_id, token_type=device, no roles.</summary>
    private static string DeviceToken(Guid sub)
    {
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("DEVELOPMENT_SECRET_KEY_CHANGE_IN_PRODUCTION_32_CHARS_MIN"));
        var token = new JwtSecurityToken(
            issuer: "https://localhost:5001",
            audience: "iam-api",
            claims: new[]
            {
                new Claim("sub", sub.ToString()),
                new Claim("tenant_id", Guid.NewGuid().ToString()),
                new Claim("token_type", "device"),
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<Guid> SeedThresholdAsync(Guid? tenantId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var t = new RiskThreshold { Id = Guid.NewGuid(), TenantId = tenantId, IsActive = false };
        db.RiskThresholds.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private async Task<Guid> SeedDeviceAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var d = new TrustedDevice { Id = Guid.NewGuid(), UserId = userId, DeviceFingerprint = $"fp-{Guid.NewGuid():N}", Name = "seeded" };
        db.TrustedDevices.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    private async Task SeedScoreAsync(Guid userId, int score = 10)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.LoginRiskScores.Add(new LoginRiskScore { Id = Guid.NewGuid(), UserId = userId, IpAddress = "93.184.216.34", RiskScore = score });
        await db.SaveChangesAsync();
    }

    private async Task<List<TrustedDevice>> DevicesOfAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.TrustedDevices.AsNoTracking().Where(d => d.UserId == userId).ToListAsync();
    }

    private async Task<int> ScoreCountAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.LoginRiskScores.CountAsync(s => s.UserId == userId);
    }

    private async Task<int> ThresholdCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.RiskThresholds.CountAsync();
    }

    private static object ThresholdBody(int low = 20, int medium = 50, int high = 75, int block = 90, int mfa = 40, Guid? tenantId = null) => new
    {
        tenantId, lowThreshold = low, mediumThreshold = medium, highThreshold = high, blockThreshold = block, requireMfaAbove = mfa, isActive = true
    };

    private static readonly string[] ThresholdActionNames =
    {
        "GET thresholds", "GET thresholds/{id}", "POST thresholds", "DELETE thresholds/{id}", "GET dashboard"
    };

    private async Task<HttpStatusCode[]> ThresholdActionsAsync(HttpClient c)
    {
        var id = await SeedThresholdAsync(Guid.NewGuid());
        return new[]
        {
            (await c.GetAsync("/api/riskassessment/thresholds")).StatusCode,
            (await c.GetAsync($"/api/riskassessment/thresholds/{id}")).StatusCode,
            (await c.PostAsJsonAsync("/api/riskassessment/thresholds", ThresholdBody())).StatusCode,
            (await c.DeleteAsync($"/api/riskassessment/thresholds/{id}")).StatusCode,
            (await c.GetAsync("/api/riskassessment/dashboard")).StatusCode,
        };
    }

    private static void AssertAll(HttpStatusCode[] statuses, HttpStatusCode expected)
    {
        for (var i = 0; i < statuses.Length; i++)
            Assert.True(expected == statuses[i], $"{ThresholdActionNames[i]} returned {statuses[i]}, expected {expected}");
    }

    // ----- thresholds and dashboard: platform security roles only --------------------------------------

    [Fact]
    public async Task Anonymous_Gets401() =>
        AssertAll(await ThresholdActionsAsync(_factory.CreateClient()), HttpStatusCode.Unauthorized);

    [Theory]
    [InlineData("User")]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    [InlineData("SystemAdmin")]
    [InlineData("ComplianceOfficer")]
    public async Task OtherRoles_Get403_OnThresholdsAndDashboard_AndNothingChanges(string role)
    {
        var before = await ThresholdCountAsync();

        AssertAll(await ThresholdActionsAsync(ClientAs(Guid.NewGuid(), role)), HttpStatusCode.Forbidden);

        Assert.Equal(before + 1, await ThresholdCountAsync()); // only the row the helper seeded itself
    }

    [Fact]
    public async Task DeviceToken_And_ServiceAccount_Get403_OnThresholdsAndDashboard()
    {
        AssertAll(await ThresholdActionsAsync(ClientWithToken(DeviceToken(Guid.NewGuid()))), HttpStatusCode.Forbidden);
        AssertAll(await ThresholdActionsAsync(ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "risk:write"))), HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("SecurityAdmin")]
    [InlineData("SuperAdmin")]
    public async Task PlatformSecurityRoles_CanManageThresholdsAndSeeTheDashboard(string role)
    {
        var statuses = await ThresholdActionsAsync(ClientAs(Guid.NewGuid(), role));

        var expected = new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.OK };
        for (var i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == statuses[i], $"{ThresholdActionNames[i]} returned {statuses[i]}, expected {expected[i]}");
    }

    // ----- threshold validation ------------------------------------------------------------------------------

    [Theory]
    [InlineData(20, 50, 75, 101, 40)]   // block above 100 can never block
    [InlineData(20, 50, 75, 1000, 40)]
    [InlineData(20, 50, 75, 0, 0)]      // block 0 blocks everyone (and breaks the ordering)
    [InlineData(20, 50, 75, 90, 100)]   // MFA above 99 can never require MFA (scores never exceed 100)
    [InlineData(20, 50, 75, 90, 150)]
    [InlineData(20, 50, 75, 90, -1)]
    [InlineData(20, 50, 75, 90, 90)]    // MFA must be below block
    [InlineData(20, 50, 75, 90, 95)]
    [InlineData(50, 20, 75, 90, 40)]    // not ascending
    [InlineData(20, 50, 90, 90, 40)]
    [InlineData(-5, 50, 75, 90, 40)]    // negative low
    public async Task InvalidThresholds_AreRejected_AndNothingIsStored(int low, int medium, int high, int block, int mfa)
    {
        var before = await ThresholdCountAsync();

        var response = await SecurityAdmin().PostAsJsonAsync("/api/riskassessment/thresholds", ThresholdBody(low, medium, high, block, mfa));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await ThresholdCountAsync());
    }

    [Theory]
    [InlineData(20, 50, 75, 90, 40)]    // defaults
    [InlineData(0, 1, 2, 3, 0)]         // lowest valid values
    [InlineData(20, 50, 75, 100, 99)]   // highest valid values
    public async Task ValidThresholds_AreAccepted(int low, int medium, int high, int block, int mfa)
    {
        var response = await SuperAdmin().PostAsJsonAsync("/api/riskassessment/thresholds", ThresholdBody(low, medium, high, block, mfa));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(block, json.GetProperty("blockThreshold").GetInt32());
        Assert.Equal(mfa, json.GetProperty("requireMfaAbove").GetInt32());
    }

    // ----- own data vs other users' --------------------------------------------------------------------------

    [Fact]
    public async Task User_SeesOnlyOwnScores_AndCannotNameAnotherUser()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        await SeedScoreAsync(me, 11);
        await SeedScoreAsync(other, 22);
        var c = ClientAs(me, "User");

        var own = await c.GetFromJsonAsync<JsonElement>("/api/riskassessment/scores?take=500");
        var named = await c.GetFromJsonAsync<JsonElement>($"/api/riskassessment/scores?userId={me}");

        Assert.All(own.EnumerateArray(), s => Assert.Equal(me, s.GetProperty("userId").GetGuid()));
        Assert.NotEmpty(own.EnumerateArray());
        Assert.All(named.EnumerateArray(), s => Assert.Equal(me, s.GetProperty("userId").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/riskassessment/scores?userId={other}")).StatusCode);
    }

    [Fact]
    public async Task User_CanListRegisterAndRemoveOwnDevices_UserIdFromTheToken()
    {
        var me = Guid.NewGuid();
        var c = ClientAs(me, "User");

        // UserId omitted: taken from the token.
        var added = await c.PostAsJsonAsync("/api/riskassessment/devices", new { deviceFingerprint = "fp-own-1", name = "My laptop" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var deviceId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Single(await DevicesOfAsync(me));

        // Own id named explicitly is fine too.
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/riskassessment/devices", new { userId = me, deviceFingerprint = "fp-own-2", name = "Phone" })).StatusCode);

        var list = await c.GetFromJsonAsync<JsonElement>($"/api/riskassessment/devices/{me}");
        Assert.Equal(2, list.GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/riskassessment/devices/{deviceId}")).StatusCode);
        Assert.Single(await DevicesOfAsync(me));
    }

    [Fact]
    public async Task User_CannotListRegisterOrRemoveAnotherUsersDevices_NothingChanges()
    {
        var me = Guid.NewGuid();
        var victim = Guid.NewGuid();
        var victimDevice = await SeedDeviceAsync(victim);
        var c = ClientAs(me, "User");

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/riskassessment/devices/{victim}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/riskassessment/devices", new { userId = victim, deviceFingerprint = "attacker-fp", name = "Evil" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync($"/api/riskassessment/devices/{victimDevice}?userId={victim}")).StatusCode);

        var devices = await DevicesOfAsync(victim);
        Assert.Single(devices);
        Assert.Equal(victimDevice, devices[0].Id);
        Assert.Empty(await DevicesOfAsync(me));
    }

    [Fact]
    public async Task OwnDeviceRemoval_DoesNotReachAnotherUsersDevice_EvenWithoutUserIdInTheQuery()
    {
        var me = Guid.NewGuid();
        var victim = Guid.NewGuid();
        var victimDevice = await SeedDeviceAsync(victim);

        // The user id comes from the token, so the victim's device id simply is not found among my devices.
        var response = await ClientAs(me, "User").DeleteAsync($"/api/riskassessment/devices/{victimDevice}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await DevicesOfAsync(victim));
    }

    [Fact]
    public async Task User_CanAssessThemselves_ButNotAnotherUser()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var c = ClientAs(me, "User");
        var before = await ScoreCountAsync(me);

        var own = await c.PostAsJsonAsync("/api/riskassessment/assess", new { ipAddress = "93.184.216.34" });
        var denied = await c.PostAsJsonAsync("/api/riskassessment/assess", new { userId = other, ipAddress = "93.184.216.34" });

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(me, (await own.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetGuid());
        Assert.True(await ScoreCountAsync(me) > before);
        Assert.Equal(0, await ScoreCountAsync(other));
    }

    // ----- platform security admins act on anyone --------------------------------------------------------------

    [Theory]
    [InlineData("SecurityAdmin")]
    [InlineData("SuperAdmin")]
    public async Task PlatformAdmin_CanActOnAnotherUsersScoresDevicesAndAssessments(string role)
    {
        var target = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();
        await SeedScoreAsync(target, 33);
        await SeedScoreAsync(someoneElse, 44);
        var existing = await SeedDeviceAsync(target);
        var c = ClientAs(Guid.NewGuid(), role);

        var all = await c.GetFromJsonAsync<JsonElement>("/api/riskassessment/scores?take=500");
        var users = all.EnumerateArray().Select(s => s.GetProperty("userId").GetGuid()).ToHashSet();
        Assert.Contains(target, users);
        Assert.Contains(someoneElse, users);

        var one = await c.GetFromJsonAsync<JsonElement>($"/api/riskassessment/scores?userId={target}");
        Assert.All(one.EnumerateArray(), s => Assert.Equal(target, s.GetProperty("userId").GetGuid()));

        Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>($"/api/riskassessment/devices/{target}")).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/riskassessment/devices", new { userId = target, deviceFingerprint = "admin-added", name = "By admin" })).StatusCode);
        Assert.Equal(2, (await DevicesOfAsync(target)).Count);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/riskassessment/devices/{existing}?userId={target}")).StatusCode);
        Assert.Single(await DevicesOfAsync(target));

        var before = await ScoreCountAsync(target);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/riskassessment/assess", new { userId = target, ipAddress = "93.184.216.34" })).StatusCode);
        Assert.True(await ScoreCountAsync(target) > before);
    }

    // ----- tokens that are not users ----------------------------------------------------------------------------

    [Fact]
    public async Task DeviceToken_And_ServiceAccount_Get403_OnPerUserEndpoints_EvenForTheirOwnSubject()
    {
        var sub = Guid.NewGuid();
        foreach (var c in new[]
        {
            ClientWithToken(DeviceToken(sub)),
            ClientWithToken(TestAuthenticationHelper.GenerateServiceAccountToken("svc", "risk:read"))
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/riskassessment/scores")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/riskassessment/devices/{sub}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/riskassessment/devices", new { deviceFingerprint = "fp", name = "n" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/riskassessment/assess", new { ipAddress = "93.184.216.34" })).StatusCode);
        }
        Assert.Empty(await DevicesOfAsync(sub));
    }

    [Fact]
    public async Task ApiKey_IsNotTheUserWhoIssuedIt()
    {
        // Driven on the controller (the test host pins the default policy to JWT). The service is never reached.
        var issuer = Guid.NewGuid();
        var key = ApiKeyPrincipal.Create(new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("D"), KeyHash = "h", KeyPrefix = "iam_test_", Name = "k",
            Scope = ApiKeyScope.Admin, TenantId = null, UserId = issuer.ToString("D"),
        });
        var controller = new RiskAssessmentController(null!, NullLogger<RiskAssessmentController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = key } },
        };

        var devices = await controller.GetTrustedDevices(issuer);
        var scores = await controller.GetScores(null, null, null);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)devices.Result!).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)scores.Result!).StatusCode);
    }
}
