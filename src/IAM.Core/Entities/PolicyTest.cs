namespace IAM.Core.Entities;

/// <summary>
/// Policy test scenario for testing policies before deployment
/// </summary>
public class PolicyTest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Policy being tested (can be draft policy not yet in production)
    /// </summary>
    public Guid? PolicyId { get; set; }
    public Policy? Policy { get; set; }

    /// <summary>
    /// Draft policy definition in JSON (for policies not yet created)
    /// </summary>
    public string? DraftPolicyJson { get; set; }

    /// <summary>
    /// Test scenarios in JSON format
    /// </summary>
    public string TestScenarios { get; set; } = "[]";

    /// <summary>
    /// Expected results in JSON format
    /// </summary>
    public string ExpectedResults { get; set; } = "[]";

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    // Navigation
    public ICollection<PolicyTestResult> TestResults { get; set; } = new List<PolicyTestResult>();
}

/// <summary>
/// Result of running a policy test
/// </summary>
public class PolicyTestResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PolicyTestId { get; set; }
    public PolicyTest PolicyTest { get; set; } = null!;

    /// <summary>
    /// When this test was run
    /// </summary>
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who executed the test
    /// </summary>
    public Guid ExecutedByUserId { get; set; }

    /// <summary>
    /// Overall test result
    /// </summary>
    public TestResultStatus Status { get; set; }

    /// <summary>
    /// Number of test scenarios passed
    /// </summary>
    public int PassedCount { get; set; }

    /// <summary>
    /// Number of test scenarios failed
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Total number of test scenarios
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Detailed test results in JSON
    /// </summary>
    public string DetailedResults { get; set; } = "[]";

    /// <summary>
    /// Test execution duration in milliseconds
    /// </summary>
    public long ExecutionTimeMs { get; set; }
}

public enum TestResultStatus
{
    Passed = 0,
    Failed = 1,
    PartiallyPassed = 2,
    Error = 3
}
