using System.Net;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Tests for the anonymous GET /api/identity-providers/public endpoint (task 4315).
/// The login page must be able to discover social login providers without a token,
/// but only active providers and only whitelisted fields may be exposed.
/// </summary>
public class IdentityProvidersPublicEndpointTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid ActiveProviderId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd01");
    private static readonly Guid InactiveProviderId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd02");

    private readonly HttpClient _client;
    private readonly IAMTestWebApplicationFactory _factory;

    public IdentityProvidersPublicEndpointTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        SeedProviders();
    }

    private void SeedProviders()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        if (db.IdentityProviders.Any(p => p.Id == ActiveProviderId))
        {
            return;
        }

        db.IdentityProviders.AddRange(
            new IdentityProvider
            {
                Id = ActiveProviderId,
                Name = "google",
                DisplayName = "Sign in with Google",
                Type = IdentityProviderType.Google,
                ClientId = "google-client-id",
                ClientSecret = "google-client-secret",
                MetadataUrl = "https://accounts.google.com/.well-known/openid-configuration",
                AttributeMapping = "{\"email\":\"email\"}",
                IsActive = true
            },
            new IdentityProvider
            {
                Id = InactiveProviderId,
                Name = "github",
                DisplayName = "Sign in with GitHub",
                Type = IdentityProviderType.GitHub,
                ClientId = "github-client-id",
                ClientSecret = "github-client-secret",
                IsActive = false
            });

        db.SaveChanges();
    }

    [Fact]
    public async Task GetPublic_WithoutAuth_ReturnsOkWithActiveProviders()
    {
        // Act
        var response = await _client.GetAsync("/api/identity-providers/public");

        // Assert
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, raw);

        using var json = JsonDocument.Parse(raw);
        var providers = json.RootElement.EnumerateArray().ToList();

        var active = Assert.Single(providers, p => p.GetProperty("id").GetGuid() == ActiveProviderId);
        Assert.Equal("google", active.GetProperty("name").GetString());
        Assert.Equal("Sign in with Google", active.GetProperty("displayName").GetString());
        Assert.Equal("Google", active.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetPublic_DoesNotIncludeInactiveProviders()
    {
        // Act
        var response = await _client.GetAsync("/api/identity-providers/public");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(ActiveProviderId, ids);
        Assert.DoesNotContain(InactiveProviderId, ids);
    }

    [Fact]
    public async Task GetPublic_ExposesOnlyWhitelistedFields()
    {
        // Act
        var response = await _client.GetAsync("/api/identity-providers/public");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var expectedKeys = new[] { "id", "name", "displayName", "type" };

        foreach (var provider in json.RootElement.EnumerateArray())
        {
            var keys = provider.EnumerateObject().Select(p => p.Name).ToList();

            // Exact whitelist: adding any new field to the public response
            // must be a deliberate decision that updates this test.
            Assert.Equal(expectedKeys.OrderBy(k => k), keys.OrderBy(k => k));

            // Explicit guard against the sensitive entity fields.
            Assert.DoesNotContain("clientSecret", keys);
            Assert.DoesNotContain("clientId", keys);
            Assert.DoesNotContain("metadataUrl", keys);
            Assert.DoesNotContain("attributeMapping", keys);
        }
    }

    [Fact]
    public async Task GetAll_WithoutAuth_StillReturnsUnauthorized()
    {
        // The authorized admin listing must remain protected; only /public is anonymous.
        var response = await _client.GetAsync("/api/identity-providers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetById_WithoutAuth_StillReturnsUnauthorized()
    {
        var response = await _client.GetAsync($"/api/identity-providers/{ActiveProviderId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
