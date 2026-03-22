using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Implementation of policy testing service (sandbox environment)
/// </summary>
public class PolicyTestingService : IPolicyTestingService
{
    private readonly IAMDbContext _context;
    private readonly IPolicyInheritanceEngine _policyEngine;
    private readonly ILogger<PolicyTestingService> _logger;

    public PolicyTestingService(
        IAMDbContext context,
        IPolicyInheritanceEngine policyEngine,
        ILogger<PolicyTestingService> logger)
    {
        _context = context;
        _policyEngine = policyEngine;
        _logger = logger;
    }

    public async Task<PolicyTest> CreateTestAsync(
        string name,
        string description,
        Guid policyId,
        string policyVersion,
        string testScenario,
        string expectedResult,
        Guid createdByUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        // Create test scenarios array
        var scenarios = new[]
        {
            new
            {
                UserId = Guid.NewGuid(),
                Resource = testScenario,
                Action = "read",
                ExpectedResult = expectedResult
            }
        };

        var test = new PolicyTest
        {
            Name = name,
            Description = description,
            PolicyId = policyId,
            TestScenarios = JsonSerializer.Serialize(scenarios),
            ExpectedResults = JsonSerializer.Serialize(new[] { expectedResult }),
            TenantId = tenantId,
            CreatedByUserId = createdByUserId
        };

        _context.PolicyTests.Add(test);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Policy test created: {TestName} for Policy:{PolicyId}",
            name, policyId);

        return test;
    }

    public async Task<PolicyTest> ExecuteTestAsync(
        Guid testId,
        CancellationToken cancellationToken = default)
    {
        var test = await _context.PolicyTests
            .Include(t => t.Policy)
            .Include(t => t.Tenant)
            .Include(t => t.TestResults)
            .FirstOrDefaultAsync(t => t.Id == testId, cancellationToken);

        if (test == null)
            throw new ArgumentException($"Policy test {testId} not found");

        var stopwatch = Stopwatch.StartNew();

        // Parse test scenarios
        var scenarios = JsonSerializer.Deserialize<List<TestScenario>>(test.TestScenarios) ?? new List<TestScenario>();
        var expectedResults = JsonSerializer.Deserialize<List<string>>(test.ExpectedResults) ?? new List<string>();

        var results = new List<ScenarioResult>();
        int passedCount = 0;
        int failedCount = 0;

        for (int i = 0; i < scenarios.Count; i++)
        {
            var scenario = scenarios[i];
            var expected = i < expectedResults.Count ? expectedResults[i] : "allow";

            try
            {
                // Execute policy evaluation in sandbox
                var evaluationResult = await _policyEngine.EvaluateAsync(
                    scenario.UserId,
                    test.TenantId,
                    scenario.Resource,
                    scenario.Action,
                    new PolicyEvaluationContext
                    {
                        IpAddress = scenario.IpAddress,
                        DeviceId = scenario.DeviceId,
                        Location = scenario.Location,
                        CustomAttributes = scenario.CustomAttributes ?? new Dictionary<string, object>()
                    },
                    cancellationToken);

                // Compare with expected result
                var expectedAllow = expected.Equals("allow", StringComparison.OrdinalIgnoreCase);
                var passed = evaluationResult.IsAllowed == expectedAllow;

                if (passed)
                    passedCount++;
                else
                    failedCount++;

                results.Add(new ScenarioResult
                {
                    ScenarioIndex = i,
                    ScenarioName = scenario.Name ?? $"Scenario {i + 1}",
                    Expected = expected,
                    Actual = evaluationResult.IsAllowed ? "allow" : "deny",
                    Passed = passed,
                    Reason = evaluationResult.Reason,
                    PolicyId = evaluationResult.PolicyId,
                    EvaluationTimeMs = evaluationResult.EvaluationTimeMs
                });
            }
            catch (Exception ex)
            {
                failedCount++;
                results.Add(new ScenarioResult
                {
                    ScenarioIndex = i,
                    ScenarioName = scenario.Name ?? $"Scenario {i + 1}",
                    Expected = expected,
                    Actual = "error",
                    Passed = false,
                    Reason = $"Test execution failed: {ex.Message}",
                    ErrorMessage = ex.ToString()
                });

                _logger.LogError(ex, "Policy test scenario {Index} failed for test {TestId}", i, testId);
            }
        }

        stopwatch.Stop();

        // Determine overall status
        TestResultStatus status;
        if (failedCount == 0)
            status = TestResultStatus.Passed;
        else if (passedCount == 0)
            status = TestResultStatus.Failed;
        else
            status = TestResultStatus.PartiallyPassed;

        // Create test result record
        var testResult = new PolicyTestResult
        {
            PolicyTestId = testId,
            ExecutedByUserId = test.CreatedByUserId, // TODO: Get from current user context
            Status = status,
            PassedCount = passedCount,
            FailedCount = failedCount,
            TotalCount = scenarios.Count,
            DetailedResults = JsonSerializer.Serialize(results),
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds
        };

        _context.PolicyTestResults.Add(testResult);
        test.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Policy test executed: {TestName}, Status: {Status}, Passed: {Passed}/{Total}",
            test.Name, status, passedCount, scenarios.Count);

        return test;
    }

    public async Task<List<PolicyTest>> ExecuteTestsForPolicyAsync(
        Guid policyId,
        CancellationToken cancellationToken = default)
    {
        var tests = await _context.PolicyTests
            .Where(t => t.PolicyId == policyId && t.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var test in tests)
        {
            await ExecuteTestAsync(test.Id, cancellationToken);
        }

        _logger.LogInformation(
            "Executed {Count} tests for policy {PolicyId}",
            tests.Count, policyId);

        return tests;
    }

    public async Task<PolicyTestCoverage> GetTestCoverageAsync(
        Guid policyId,
        CancellationToken cancellationToken = default)
    {
        var policy = await _context.Policies
            .FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);

        if (policy == null)
            throw new ArgumentException($"Policy {policyId} not found");

        var tests = await _context.PolicyTests
            .Include(t => t.TestResults)
            .Where(t => t.PolicyId == policyId && t.IsActive)
            .ToListAsync(cancellationToken);

        var totalTests = tests.Count;
        var testsWithResults = tests.Count(t => t.TestResults.Any());
        var latestResults = tests
            .SelectMany(t => t.TestResults.OrderByDescending(r => r.ExecutedAt).Take(1))
            .ToList();

        var passedTests = latestResults.Count(r => r.Status == TestResultStatus.Passed);
        var failedTests = latestResults.Count(r => r.Status == TestResultStatus.Failed);
        var partiallyPassedTests = latestResults.Count(r => r.Status == TestResultStatus.PartiallyPassed);

        // Calculate coverage score
        int coverageScore = 0;
        if (totalTests > 0)
        {
            var executionRate = (double)testsWithResults / totalTests * 100;
            var passRate = testsWithResults > 0 ? (double)passedTests / testsWithResults * 100 : 0;
            coverageScore = (int)((executionRate * 0.4) + (passRate * 0.6)); // Weighted: 40% execution, 60% pass rate
        }

        var coverage = new PolicyTestCoverage
        {
            PolicyId = policyId,
            TotalTests = totalTests,
            PassingTests = passedTests,
            FailingTests = failedTests + partiallyPassedTests,
            UniqueScenarios = tests.Sum(t => JsonSerializer.Deserialize<List<TestScenario>>(t.TestScenarios)?.Count ?? 0),
            LastTestRun = latestResults.Any() ? latestResults.Max(r => r.ExecutedAt) : (DateTime?)null
        };

        return coverage;
    }

    public async Task<List<PolicyTest>> GetTestResultsAsync(
        Guid? policyId = null,
        bool? passedOnly = null,
        DateTime? since = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var query = _context.PolicyTests
            .Include(t => t.Policy)
            .Include(t => t.TestResults)
            .AsQueryable();

        if (policyId.HasValue)
            query = query.Where(t => t.PolicyId == policyId.Value);

        if (since.HasValue)
            query = query.Where(t => t.TestResults.Any(r => r.ExecutedAt >= since.Value));

        if (passedOnly.HasValue)
        {
            var targetStatus = passedOnly.Value ? TestResultStatus.Passed : TestResultStatus.Failed;
            query = query.Where(t => t.TestResults.Any(r => r.Status == targetStatus));
        }

        return await query
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(skip)
            .Take(Math.Min(take, 1000))
            .ToListAsync(cancellationToken);
    }

    public async Task<PolicyTestComparison> CompareVersionsAsync(
        Guid policyId,
        string version1,
        string version2,
        CancellationToken cancellationToken = default)
    {
        // For now, we'll compare test results over time rather than versions
        // In a full implementation, you'd store policy versions and compare their test results

        var tests = await _context.PolicyTests
            .Include(t => t.TestResults)
            .Include(t => t.Policy)
            .Where(t => t.PolicyId == policyId && t.IsActive)
            .ToListAsync(cancellationToken);

        var comparison = new PolicyTestComparison
        {
            PolicyId = policyId,
            Version1 = version1,
            Version2 = version2,
            TestsInVersion1 = tests.Count,
            TestsInVersion2 = tests.Count,
            RegressionCount = 0, // Would need version history to implement properly
            Regressions = new List<string>(),
            Improvements = new List<string>()
        };

        _logger.LogInformation(
            "Compared policy versions: Policy:{PolicyId}, Tests:{Count}",
            policyId, tests.Count);

        return comparison;
    }

    private string GenerateCoverageRecommendation(int totalTests, int passedTests, int failedTests, int coverageScore)
    {
        if (totalTests == 0)
            return "No tests defined. Create comprehensive test scenarios covering all policy rules.";

        if (coverageScore < 50)
            return "Low test coverage. Add more test scenarios and ensure all tests pass.";

        if (failedTests > 0)
            return $"{failedTests} test(s) failing. Review and fix policy configuration or update test expectations.";

        if (coverageScore < 80)
            return "Good coverage. Consider adding edge case scenarios for comprehensive testing.";

        return "Excellent test coverage. Policy is well-tested and verified.";
    }
}

/// <summary>
/// Test scenario for policy testing
/// </summary>
public class TestScenario
{
    public string? Name { get; set; }
    public Guid UserId { get; set; }
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? DeviceId { get; set; }
    public string? Location { get; set; }
    public Dictionary<string, object>? CustomAttributes { get; set; }
}

/// <summary>
/// Result of executing a test scenario
/// </summary>
public class ScenarioResult
{
    public int ScenarioIndex { get; set; }
    public string ScenarioName { get; set; } = string.Empty;
    public string Expected { get; set; } = string.Empty;
    public string Actual { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? Reason { get; set; }
    public Guid? PolicyId { get; set; }
    public long EvaluationTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
}
