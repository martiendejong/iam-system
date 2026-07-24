using System.Net;
using IAM.API.Tests.Infrastructure;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Health check tests to verify basic connectivity
/// </summary>
public class HealthCheckTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthCheckTests(IAMTestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthCheck_ReturnsHealthy()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", content);
    }

    [Fact]
    public async Task HealthCheck_WithAuth_ReturnsHealthy()
    {
        // Arrange
        var token = TestAuthenticationHelper.GenerateAdminToken();
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
