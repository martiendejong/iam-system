using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for AuditController
/// Tests all 5 endpoints with various scenarios
/// </summary>
public class AuditControllerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly IAMTestWebApplicationFactory _factory;

    public AuditControllerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateAdminToken());
    }

    [Fact]
    public async Task GetAuditEvents_ReturnsSuccess()
    {
        // Act
        var response = await _client.GetAsync("/api/audit/events");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<PolicyAuditEvent>>();
        Assert.NotNull(events);
    }

    [Fact]
    public async Task GetAuditEvents_WithFiltering_ReturnsFilteredResults()
    {
        // Arrange
        var startDate = DateTime.UtcNow.AddDays(-7).ToString("O");
        var endDate = DateTime.UtcNow.ToString("O");

        // Act
        var response = await _client.GetAsync($"/api/audit/events?startDate={startDate}&endDate={endDate}&take=50");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<PolicyAuditEvent>>();
        Assert.NotNull(events);
    }

    [Fact]
    public async Task GetComplianceStatistics_ReturnsStatistics()
    {
        // Arrange
        var startDate = DateTime.UtcNow.AddDays(-30).ToString("O");
        var endDate = DateTime.UtcNow.ToString("O");

        // Act
        var response = await _client.GetAsync($"/api/audit/statistics?startDate={startDate}&endDate={endDate}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stats = await response.Content.ReadFromJsonAsync<ComplianceStatistics>();
        Assert.NotNull(stats);
        Assert.True(stats.TotalPolicies >= 0);
    }

    [Fact]
    public async Task GetComplianceStatistics_WithoutDates_ReturnsBadRequest()
    {
        // Act
        var response = await _client.GetAsync("/api/audit/statistics");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GenerateComplianceReport_CreatesReport()
    {
        // Arrange
        var request = new
        {
            Framework = "SOC2",
            PeriodStart = DateTime.UtcNow.AddDays(-30),
            PeriodEnd = DateTime.UtcNow,
            TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111")
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/audit/reports", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ComplianceReport>();
        Assert.NotNull(report);
        Assert.Equal("SOC2", report.Framework);
        Assert.True(report.Score >= 0 && report.Score <= 100);
    }

    [Fact]
    public async Task GenerateComplianceReport_WithoutFramework_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            Framework = "",
            PeriodStart = DateTime.UtcNow.AddDays(-30),
            PeriodEnd = DateTime.UtcNow
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/audit/reports", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DetectAnomalies_ReturnsAnomalies()
    {
        // Act
        var response = await _client.GetAsync("/api/audit/anomalies");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var anomalies = await response.Content.ReadFromJsonAsync<List<PolicyAuditEvent>>();
        Assert.NotNull(anomalies);
    }

    [Fact]
    public async Task DetectAnomalies_WithSinceParameter_ReturnsFilteredAnomalies()
    {
        // Arrange
        var since = DateTime.UtcNow.AddDays(-1).ToString("O");

        // Act
        var response = await _client.GetAsync($"/api/audit/anomalies?since={since}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DetectAnomalies_RequiresSecurityAdminRole()
    {
        // Arrange
        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        // Act
        var response = await userClient.GetAsync("/api/audit/anomalies");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CleanupOldAuditEvents_DeletesEvents()
    {
        // Act
        var response = await _client.DeleteAsync("/api/audit/cleanup?retentionDays=90");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Dictionary<string, int>>();
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("deletedCount"));
    }

    [Fact]
    public async Task CleanupOldAuditEvents_InvalidRetention_ReturnsBadRequest()
    {
        // Act - Too short retention
        var response1 = await _client.DeleteAsync("/api/audit/cleanup?retentionDays=15");

        // Act - Too long retention
        var response2 = await _client.DeleteAsync("/api/audit/cleanup?retentionDays=4000");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response1.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);
    }

    [Fact]
    public async Task CleanupOldAuditEvents_RequiresSystemAdminRole()
    {
        // Arrange
        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        // Act
        var response = await userClient.DeleteAsync("/api/audit/cleanup?retentionDays=90");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
