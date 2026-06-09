using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for PolicyTestingController
/// Tests all 6 endpoints with various scenarios
/// </summary>
public class PolicyTestingControllerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly IAMTestWebApplicationFactory _factory;
    private readonly Guid _testPolicyId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private readonly Guid _testTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public PolicyTestingControllerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateAdminToken());
    }

    [Fact]
    public async Task CreateTest_CreatesNewPolicyTest()
    {
        // Arrange
        var request = new
        {
            Name = "Test Access to Resource",
            Description = "Tests if user can read test resource",
            PolicyId = _testPolicyId,
            PolicyVersion = "1.0.0",
            TestScenario = "test:resource",
            ExpectedResult = "allow",
            TenantId = _testTenantId
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/policytesting/tests", request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var test = await response.Content.ReadFromJsonAsync<PolicyTest>();
        Assert.NotNull(test);
        Assert.Equal("Test Access to Resource", test.Name);
    }

    [Fact]
    public async Task GetTest_ReturnsExistingTest()
    {
        // Arrange - Create a test first
        var createRequest = new
        {
            Name = "Get Test Scenario",
            Description = "Test for retrieval",
            PolicyId = _testPolicyId,
            PolicyVersion = "1.0.0",
            TestScenario = "test:resource",
            ExpectedResult = "allow",
            TenantId = _testTenantId
        };
        var createResponse = await _client.PostAsJsonAsync("/api/policytesting/tests", createRequest);
        var createdTest = await createResponse.Content.ReadFromJsonAsync<PolicyTest>();

        // Act
        var response = await _client.GetAsync($"/api/policytesting/tests/{createdTest!.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var test = await response.Content.ReadFromJsonAsync<PolicyTest>();
        Assert.NotNull(test);
        Assert.Equal(createdTest.Id, test.Id);
    }

    [Fact]
    public async Task GetTest_NonExistentId_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/policytesting/tests/{nonExistentId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteTest_RunsTestAndReturnsResults()
    {
        // Arrange - Create a test first
        var createRequest = new
        {
            Name = "Execute Test Scenario",
            Description = "Test for execution",
            PolicyId = _testPolicyId,
            PolicyVersion = "1.0.0",
            TestScenario = "test:resource",
            ExpectedResult = "allow",
            TenantId = _testTenantId
        };
        var createResponse = await _client.PostAsJsonAsync("/api/policytesting/tests", createRequest);
        var createdTest = await createResponse.Content.ReadFromJsonAsync<PolicyTest>();

        // Act
        var response = await _client.PostAsync($"/api/policytesting/tests/{createdTest!.Id}/execute", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var test = await response.Content.ReadFromJsonAsync<PolicyTest>();
        Assert.NotNull(test);
    }

    [Fact]
    public async Task ExecuteTest_NonExistentId_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsync($"/api/policytesting/tests/{nonExistentId}/execute", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteTestsForPolicy_ExecutesAllTests()
    {
        // Arrange - Create multiple tests for the policy
        for (int i = 0; i < 3; i++)
        {
            var request = new
            {
                Name = $"Policy Test {i}",
                Description = $"Test scenario {i}",
                PolicyId = _testPolicyId,
                PolicyVersion = "1.0.0",
                TestScenario = "test:resource",
                ExpectedResult = "allow",
                TenantId = _testTenantId
            };
            await _client.PostAsJsonAsync("/api/policytesting/tests", request);
        }

        // Act
        var response = await _client.PostAsync($"/api/policytesting/policies/{_testPolicyId}/execute-tests", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tests = await response.Content.ReadFromJsonAsync<List<PolicyTest>>();
        Assert.NotNull(tests);
        Assert.True(tests.Count >= 3);
    }

    [Fact]
    public async Task GetTestCoverage_ReturnsCoverageMetrics()
    {
        // Act
        var response = await _client.GetAsync($"/api/policytesting/policies/{_testPolicyId}/coverage");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var coverage = await response.Content.ReadFromJsonAsync<PolicyTestCoverage>();
        Assert.NotNull(coverage);
        Assert.Equal(_testPolicyId, coverage.PolicyId);
        Assert.True(coverage.PassRate >= 0 && coverage.PassRate <= 100);
    }

    [Fact]
    public async Task GetTestCoverage_NonExistentPolicy_ReturnsNotFound()
    {
        // Arrange
        var nonExistentPolicyId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/policytesting/policies/{nonExistentPolicyId}/coverage");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTestResults_ReturnsAllTests()
    {
        // Act
        var response = await _client.GetAsync("/api/policytesting/tests");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tests = await response.Content.ReadFromJsonAsync<List<PolicyTest>>();
        Assert.NotNull(tests);
    }

    [Fact]
    public async Task GetTestResults_WithFiltering_ReturnsFilteredTests()
    {
        // Act
        var response = await _client.GetAsync($"/api/policytesting/tests?policyId={_testPolicyId}&take=10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tests = await response.Content.ReadFromJsonAsync<List<PolicyTest>>();
        Assert.NotNull(tests);
        Assert.All(tests, t => Assert.Equal(_testPolicyId, t.PolicyId));
    }

    [Fact]
    public async Task CompareVersions_ReturnsComparison()
    {
        // Act
        var response = await _client.GetAsync($"/api/policytesting/policies/{_testPolicyId}/compare?version1=1.0.0&version2=1.0.1");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var comparison = await response.Content.ReadFromJsonAsync<PolicyTestComparison>();
        Assert.NotNull(comparison);
        Assert.Equal(_testPolicyId, comparison.PolicyId);
        Assert.Equal("1.0.0", comparison.Version1);
        Assert.Equal("1.0.1", comparison.Version2);
    }

    [Fact]
    public async Task CompareVersions_WithoutVersions_ReturnsBadRequest()
    {
        // Act
        var response = await _client.GetAsync($"/api/policytesting/policies/{_testPolicyId}/compare");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
