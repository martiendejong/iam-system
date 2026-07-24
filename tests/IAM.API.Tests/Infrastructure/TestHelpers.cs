namespace IAM.API.Tests.Infrastructure;

/// <summary>
/// Helper methods for integration tests
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Get admin token asynchronously (wraps synchronous token generation)
    /// </summary>
    public static Task<string> GetAdminTokenAsync(HttpClient client)
    {
        return Task.FromResult(TestAuthenticationHelper.GenerateAdminToken());
    }

    /// <summary>
    /// Get user token asynchronously (wraps synchronous token generation)
    /// </summary>
    public static Task<string> GetUserTokenAsync(HttpClient client)
    {
        return Task.FromResult(TestAuthenticationHelper.GenerateUserToken());
    }
}
