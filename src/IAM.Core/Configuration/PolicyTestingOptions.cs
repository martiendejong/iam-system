namespace IAM.Core.Configuration;

/// <summary>
/// Limits of the policy-testing API (task 5486). Configure under "PolicyTesting" in appsettings.
/// </summary>
public class PolicyTestingOptions
{
    public const string SectionName = "PolicyTesting";

    /// <summary>
    /// The most tests one "execute all tests of a policy" run executes (oldest first); the rest wait for the next
    /// run. Default 50. Config key: PolicyTesting:MaxTestsPerRun.
    /// </summary>
    public int MaxTestsPerRun { get; set; } = 50;
}
