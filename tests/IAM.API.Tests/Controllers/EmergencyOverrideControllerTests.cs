using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for EmergencyOverrideController
/// Tests all 7 endpoints with security and authorization scenarios
/// </summary>
public class EmergencyOverrideControllerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly Guid _testTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _testUserId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    public EmergencyOverrideControllerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateAdminToken());
    }

    [Fact]
    public async Task ActivateOverride_CreatesNewOverride()
    {
        // Arrange
        var request = new
        {
            TenantId = _testTenantId,
            OverrideType = EmergencyOverrideType.FullAccess,
            Justification = "Production incident - database unreachable",
            Severity = EmergencySeverity.High,
            DurationMinutes = 60,
            IncidentTicketId = "INC-12345",
            DeviceId = "DEVICE-001"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/emergencyoverride/activate", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var emergencyOverride = await response.Content.ReadFromJsonAsync<EmergencyOverride>();
        Assert.NotNull(emergencyOverride);
        Assert.Equal("Production incident - database unreachable", emergencyOverride.Justification);
        Assert.Equal(EmergencyOverrideStatus.Active, emergencyOverride.Status);
    }

    [Fact]
    public async Task ActivateOverride_WithoutJustification_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            TenantId = _testTenantId,
            OverrideType = EmergencyOverrideType.FullAccess,
            Justification = "",
            Severity = EmergencySeverity.High,
            DurationMinutes = 60
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/emergencyoverride/activate", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ActivateOverride_InvalidDuration_ReturnsBadRequest()
    {
        // Arrange - Duration too long
        var request = new
        {
            TenantId = _testTenantId,
            OverrideType = EmergencyOverrideType.FullAccess,
            Justification = "Test justification",
            Severity = EmergencySeverity.High,
            DurationMinutes = 2000 // > 1440 (24 hours)
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/emergencyoverride/activate", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ActivateOverride_RequiresEmergencyAccessRole()
    {
        // Arrange
        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        var request = new
        {
            TenantId = _testTenantId,
            OverrideType = EmergencyOverrideType.FullAccess,
            Justification = "Test justification",
            Severity = EmergencySeverity.High,
            DurationMinutes = 60
        };

        // Act
        var response = await userClient.PostAsJsonAsync("/api/emergencyoverride/activate", request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeactivateOverride_DeactivatesActiveOverride()
    {
        // Arrange - Create an override first
        var activateRequest = new
        {
            TenantId = _testTenantId,
            OverrideType = EmergencyOverrideType.FullAccess,
            Justification = "Test deactivation",
            Severity = EmergencySeverity.Medium,
            DurationMinutes = 120
        };
        var activateResponse = await _client.PostAsJsonAsync("/api/emergencyoverride/activate", activateRequest);
        var emergencyOverride = await activateResponse.Content.ReadFromJsonAsync<EmergencyOverride>();

        // Act
        var response = await _client.PostAsync($"/api/emergencyoverride/{emergencyOverride!.Id}/deactivate", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deactivated = await response.Content.ReadFromJsonAsync<EmergencyOverride>();
        Assert.NotNull(deactivated);
        Assert.Equal(EmergencyOverrideStatus.Deactivated, deactivated.Status);
    }

    [Fact]
    public async Task DeactivateOverride_NonExistentId_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsync($"/api/emergencyoverride/{nonExistentId}/deactivate", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HasActiveOverride_ChecksForActiveOverrides()
    {
        // Arrange - Create an active override
        var request = new
        {
            TenantId = _testTenantId,
            OverrideType = EmergencyOverrideType.FullAccess,
            Justification = "Test active check",
            Severity = EmergencySeverity.Medium,
            DurationMinutes = 60
        };
        await _client.PostAsJsonAsync("/api/emergencyoverride/activate", request);

        // Act
        var response = await _client.GetAsync($"/api/emergencyoverride/active/check?userId={_testUserId}&tenantId={_testTenantId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>();
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("hasActiveOverride"));
    }

    [Fact]
    public async Task GetActiveOverrides_ReturnsActiveOverrides()
    {
        // Act
        var response = await _client.GetAsync("/api/emergencyoverride/active");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overrides = await response.Content.ReadFromJsonAsync<List<EmergencyOverride>>();
        Assert.NotNull(overrides);
    }

    [Fact]
    public async Task GetActiveOverrides_WithTenantFilter_ReturnsFilteredOverrides()
    {
        // Act
        var response = await _client.GetAsync($"/api/emergencyoverride/active?tenantId={_testTenantId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overrides = await response.Content.ReadFromJsonAsync<List<EmergencyOverride>>();
        Assert.NotNull(overrides);
        Assert.All(overrides, o => Assert.Equal(_testTenantId, o.TenantId));
    }

    [Fact]
    public async Task GetOverrideHistory_ReturnsHistory()
    {
        // Act
        var response = await _client.GetAsync("/api/emergencyoverride/history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<List<EmergencyOverride>>();
        Assert.NotNull(history);
    }

    [Fact]
    public async Task GetOverrideHistory_WithFiltering_ReturnsFilteredHistory()
    {
        // Act
        var startDate = DateTime.UtcNow.AddDays(-7).ToString("O");
        var endDate = DateTime.UtcNow.ToString("O");
        var response = await _client.GetAsync($"/api/emergencyoverride/history?userId={_testUserId}&startDate={startDate}&endDate={endDate}&take=50");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<List<EmergencyOverride>>();
        Assert.NotNull(history);
    }

    [Fact]
    public async Task ReviewOverride_UpdatesReviewStatus()
    {
        // Arrange - Create an override first
        var activateRequest = new
        {
            TenantId = _testTenantId,
            OverrideType = EmergencyOverrideType.FullAccess,
            Justification = "Test review",
            Severity = EmergencySeverity.Medium,
            DurationMinutes = 60
        };
        var activateResponse = await _client.PostAsJsonAsync("/api/emergencyoverride/activate", activateRequest);
        var emergencyOverride = await activateResponse.Content.ReadFromJsonAsync<EmergencyOverride>();

        var reviewRequest = new
        {
            ApprovalStatus = EmergencyApprovalStatus.Approved,
            ReviewComments = "Override was justified and properly documented"
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/emergencyoverride/{emergencyOverride!.Id}/review", reviewRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reviewed = await response.Content.ReadFromJsonAsync<EmergencyOverride>();
        Assert.NotNull(reviewed);
        Assert.Equal(EmergencyApprovalStatus.Approved, reviewed.ApprovalStatus);
    }

    [Fact]
    public async Task ReviewOverride_RequiresSecurityAdminOrComplianceOfficerRole()
    {
        // Arrange
        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        // Create override as admin
        var activateRequest = new
        {
            TenantId = _testTenantId,
            OverrideType = EmergencyOverrideType.FullAccess,
            Justification = "Test authorization",
            Severity = EmergencySeverity.Medium,
            DurationMinutes = 60
        };
        var activateResponse = await _client.PostAsJsonAsync("/api/emergencyoverride/activate", activateRequest);
        var emergencyOverride = await activateResponse.Content.ReadFromJsonAsync<EmergencyOverride>();

        var reviewRequest = new
        {
            ApprovalStatus = EmergencyApprovalStatus.Approved,
            ReviewComments = "Test comment"
        };

        // Act - Try to review as regular user
        var response = await userClient.PostAsJsonAsync($"/api/emergencyoverride/{emergencyOverride!.Id}/review", reviewRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetPendingReviews_ReturnsPendingOverrides()
    {
        // Act
        var response = await _client.GetAsync("/api/emergencyoverride/pending-review");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = await response.Content.ReadFromJsonAsync<List<EmergencyOverride>>();
        Assert.NotNull(pending);
        Assert.All(pending, o => Assert.Equal(EmergencyApprovalStatus.PendingReview, o.ApprovalStatus));
    }
}
