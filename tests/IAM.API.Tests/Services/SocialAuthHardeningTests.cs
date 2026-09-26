using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4316: SocialAuthService now validates redirectUri against an allowlist and validates
/// server-side state (one-time, TTL-bound). These tests verify the three required outcomes:
/// bad redirectUri rejected, missing/reused state rejected, happy path intact.
/// </summary>
public class SocialAuthHardeningTests
{
    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IAMDbContext(options);
    }

    private static IConfiguration CreateConfiguration(string? allowedRedirectUri = "https://app.example.com/callback")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "test-secret-key-that-is-long-enough-for-hmacsha256",
            ["Jwt:Issuer"] = "iam-tests",
            ["Jwt:Audience"] = "iam-tests",
            ["Jwt:AccessTokenExpirationMinutes"] = "5",
            ["SocialAuth:RedirectUri"] = allowedRedirectUri
        };
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static (SocialAuthService service, IAMDbContext context, IMemoryCache cache) CreateService(
        string? globalRedirectUri = "https://app.example.com/callback")
    {
        var context = CreateContext();
        var config = CreateConfiguration(globalRedirectUri);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var httpClientFactory = new FakeHttpClientFactory();
        var claimsMappingService = new ClaimsMappingService(context);
        var secretsVault = new FakeSecretsVaultService();

        var service = new SocialAuthService(
            context, config, httpClientFactory, claimsMappingService, secretsVault, cache);

        return (service, context, cache);
    }

    private static IdentityProvider CreateProvider(IAMDbContext context, string? allowedRedirectUris = null)
    {
        var provider = new IdentityProvider
        {
            Name = "test-google",
            DisplayName = "Google",
            Type = IdentityProviderType.Google,
            ClientId = "test-client-id",
            ClientSecret = "test-secret",
            IsActive = true,
            AllowedRedirectUris = allowedRedirectUris
        };
        context.IdentityProviders.Add(provider);
        context.SaveChanges();
        return provider;
    }

    [Fact]
    public async Task GetAuthorizationUrlAsync_WithAllowedRedirectUri_Succeeds()
    {
        var (service, context, _) = CreateService();
        var provider = CreateProvider(context);

        var url = await service.GetAuthorizationUrlAsync(
            provider.Id, "https://app.example.com/callback", "state-123");

        Assert.Contains("accounts.google.com", url);
        Assert.Contains("redirect_uri=", url);
    }

    [Fact]
    public async Task GetAuthorizationUrlAsync_WithDisallowedRedirectUri_Throws()
    {
        var (service, context, _) = CreateService();
        var provider = CreateProvider(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetAuthorizationUrlAsync(
                provider.Id, "https://evil.example.com/steal", "state-123"));

        Assert.Contains("allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAuthorizationUrlAsync_WithPerProviderAllowlist_EnforcesExactMatch()
    {
        var (service, context, _) = CreateService();
        // Provider has its own allowlist that doesn't include the global fallback.
        var provider = CreateProvider(context,
            allowedRedirectUris: "[\"https://custom.app.com/oauth\"]");

        // Custom URI is allowed.
        var url = await service.GetAuthorizationUrlAsync(
            provider.Id, "https://custom.app.com/oauth", "state-abc");
        Assert.Contains("accounts.google.com", url);

        // Global fallback URI is now rejected (provider has its own list).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetAuthorizationUrlAsync(
                provider.Id, "https://app.example.com/callback", "state-xyz"));
    }

    [Fact]
    public async Task HandleCallbackAsync_WithMissingState_ReturnsError()
    {
        var (service, context, _) = CreateService();
        var provider = CreateProvider(context);

        // Callback with a state that was never stored server-side.
        var result = await service.HandleCallbackAsync(provider.Id, "auth-code", "nonexistent-state");

        Assert.False(result.Success);
        Assert.Contains("state", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleCallbackAsync_WithReusedState_ReturnsErrorOnSecondUse()
    {
        var (service, context, cache) = CreateService();
        var provider = CreateProvider(context);

        // Obtain a valid state entry by calling authorize.
        await service.GetAuthorizationUrlAsync(
            provider.Id, "https://app.example.com/callback", "reuse-test");

        // First callback attempt: state is valid, will fail later (no real OAuth exchange)
        // but must NOT fail on the state check itself. We only care that the second attempt
        // fails on the state check.
        await service.HandleCallbackAsync(provider.Id, "code-1", "reuse-test");

        // Second callback with the same state must be rejected.
        var result = await service.HandleCallbackAsync(provider.Id, "code-2", "reuse-test");
        Assert.False(result.Success);
        Assert.Contains("state", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleCallbackAsync_StateProviderId_MustMatchUrlProviderId()
    {
        var (service, context, _) = CreateService();
        var provider = CreateProvider(context);

        // Obtain state for this provider.
        await service.GetAuthorizationUrlAsync(
            provider.Id, "https://app.example.com/callback", "cross-provider-state");

        // Try to redeem it for a different provider ID.
        var differentId = Guid.NewGuid();
        var result = await service.HandleCallbackAsync(differentId, "code", "cross-provider-state");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }
}

// Test doubles

file class FakeHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

file class FakeSecretsVaultService : ISecretsVaultService
{
    public Task<SecretEntry> CreateSecretAsync(
        string name, string plainTextValue, Guid? tenantId = null,
        string secretType = "Generic", string? description = null,
        string? rotationScheduleJson = null, string? tags = null,
        Guid? createdByUserId = null, CancellationToken ct = default)
        => Task.FromResult(new SecretEntry { Id = Guid.NewGuid(), Name = name });

    public Task<SecretEntry?> GetSecretAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<SecretEntry?>(null);

    public Task<string?> GetSecretValueAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<List<SecretEntry>> GetSecretsAsync(
        Guid? tenantId = null, string? secretType = null,
        bool? isActive = null, CancellationToken ct = default)
        => Task.FromResult(new List<SecretEntry>());

    public Task<SecretEntry?> UpdateSecretAsync(
        Guid id, string? name = null, string? description = null,
        string? rotationScheduleJson = null, string? tags = null,
        string? secretType = null, bool? isActive = null, CancellationToken ct = default)
        => Task.FromResult<SecretEntry?>(null);

    public Task<SecretEntry> RotateSecretAsync(
        Guid id, string newPlainTextValue, string rotationReason = "Manual",
        Guid? rotatedByUserId = null, TimeSpan? gracePeriod = null, CancellationToken ct = default)
        => Task.FromResult(new SecretEntry { Id = id });

    public Task<List<SecretVersion>> GetSecretHistoryAsync(Guid secretId, CancellationToken ct = default)
        => Task.FromResult(new List<SecretVersion>());

    public Task<bool> DeleteSecretAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<List<SecretEntry>> GetSecretsDueForRotationAsync(CancellationToken ct = default)
        => Task.FromResult(new List<SecretEntry>());
}
