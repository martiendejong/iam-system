using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for policy testing in isolated sandbox environment
/// </summary>
public interface IPolicyTestingService
{
    /// <summary>
    /// Create a new policy test scenario
    /// </summary>
    Task<PolicyTest> CreateTestAsync(
        string name,
        string description,
        Guid policyId,
        string policyVersion,
        string testScenario,
        string expectedResult,
        Guid createdByUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a policy test in sandbox
    /// </summary>
    Task<PolicyTest> ExecuteTestAsync(
        Guid testId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute all tests for a specific policy
    /// </summary>
    Task<List<PolicyTest>> ExecuteTestsForPolicyAsync(
        Guid policyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get test coverage for a policy
    /// </summary>
    Task<PolicyTestCoverage> GetTestCoverageAsync(
        Guid policyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get test results with filtering
    /// </summary>
    Task<List<PolicyTest>> GetTestResultsAsync(
        Guid? policyId = null,
        bool? passedOnly = null,
        DateTime? since = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare test results across policy versions
    /// </summary>
    Task<PolicyTestComparison> CompareVersionsAsync(
        Guid policyId,
        string version1,
        string version2,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Policy test coverage statistics
/// </summary>
public class PolicyTestCoverage
{
    public Guid PolicyId { get; set; }
    public int TotalTests { get; set; }
    public int PassingTests { get; set; }
    public int FailingTests { get; set; }
    public double PassRate => TotalTests > 0 ? (double)PassingTests / TotalTests * 100 : 0;
    public int UniqueScenarios { get; set; }
    public DateTime? LastTestRun { get; set; }
}

/// <summary>
/// Comparison between two policy versions
/// </summary>
public class PolicyTestComparison
{
    public Guid PolicyId { get; set; }
    public string Version1 { get; set; } = string.Empty;
    public string Version2 { get; set; } = string.Empty;
    public int TestsInVersion1 { get; set; }
    public int TestsInVersion2 { get; set; }
    public int RegressionCount { get; set; } // Tests that passed in v1 but fail in v2
    public List<string> Regressions { get; set; } = new();
    public List<string> Improvements { get; set; } = new();
}
