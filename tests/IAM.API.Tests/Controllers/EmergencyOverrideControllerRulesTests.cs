using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>Task 5632: self-review, foreign tenant and cross-user check rules of the emergency override endpoints.</summary>
public class EmergencyOverrideControllerRulesTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _admin;
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly Guid _tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _adminUserId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ForeignTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public EmergencyOverrideControllerRulesTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClient();
        _admin.AddAuthorizationHeader(TestAuthenticationHelper.GenerateAdminToken());
    }

    private HttpClient ClientWith(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(userId, "u@test.com", roles));
        return client;
    }

    private object ActivateBody(Guid tenantId, string justification) => new
    {
        TenantId = tenantId,
        OverrideType = EmergencyOverrideType.FullAccess,
        Justification = justification,
        Severity = EmergencySeverity.High,
        DurationMinutes = 60
    };

    [Fact]
    public async Task Review_ByActivatingUser_IsRejected_AndOverrideStaysUnreviewed()
    {
        var activate = await _admin.PostAsJsonAsync("/api/emergencyoverride/activate", ActivateBody(_tenantId, "Self review attempt"));
        var activated = (await activate.Content.ReadFromJsonAsync<EmergencyOverride>())!;

        var response = await _admin.PostAsJsonAsync($"/api/emergencyoverride/{activated.Id}/review",
            new { ApprovalStatus = EmergencyApprovalStatus.Approved, ReviewComments = "self" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("someone other than", await response.Content.ReadAsStringAsync());

        var pending = await (await _admin.GetAsync("/api/emergencyoverride/pending-review"))
            .Content.ReadFromJsonAsync<List<EmergencyOverride>>();
        Assert.Contains(pending!, o => o.Id == activated.Id && o.ReviewedByUserId == null);
    }

    [Fact]
    public async Task Review_ByAnotherUser_Succeeds()
    {
        var activate = await _admin.PostAsJsonAsync("/api/emergencyoverride/activate", ActivateBody(_tenantId, "Second person review"));
        var activated = (await activate.Content.ReadFromJsonAsync<EmergencyOverride>())!;

        var reviewer = ClientWith(Guid.NewGuid(), "SecurityAdmin");
        var response = await reviewer.PostAsJsonAsync($"/api/emergencyoverride/{activated.Id}/review",
            new { ApprovalStatus = EmergencyApprovalStatus.Approved, ReviewComments = "ok" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Activate_ForTenantCallerCannotManage_ReturnsForbidden()
    {
        var response = await _admin.PostAsJsonAsync("/api/emergencyoverride/activate", ActivateBody(ForeignTenantId, "Wrong tenant"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(ForeignTenantId.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Activate_SuperAdmin_MayActivateForAnyTenant()
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(
            Guid.NewGuid(), "root@test.com", new[] { "SuperAdmin", "EmergencyAccess" }, null));

        var response = await client.PostAsJsonAsync("/api/emergencyoverride/activate", ActivateBody(ForeignTenantId, "Cross-tenant incident"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Check_AboutAnotherUser_ByNonAdmin_ReturnsForbidden()
    {
        var client = ClientWith(Guid.NewGuid(), "User");

        var response = await client.GetAsync($"/api/emergencyoverride/active/check?userId={_adminUserId}&tenantId={_tenantId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Check_AboutSelf_ByNonAdmin_IsAllowed()
    {
        var me = Guid.NewGuid();
        var client = ClientWith(me, "User");

        var response = await client.GetAsync($"/api/emergencyoverride/active/check?userId={me}&tenantId={_tenantId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("SecurityAdmin")]
    [InlineData("SystemAdmin")]
    public async Task Check_AboutAnotherUser_BySecurityOrSystemAdmin_IsAllowed(string role)
    {
        var client = ClientWith(Guid.NewGuid(), role);

        var response = await client.GetAsync($"/api/emergencyoverride/active/check?userId={_adminUserId}&tenantId={_tenantId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
