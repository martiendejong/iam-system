using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IAM.API.Tests;

/// <summary>
/// Public self-registration is closed by default (Registration:PublicRegistrationEnabled=false).
/// Accounts are created via invitations or by an admin.
/// </summary>
public class RegistrationGateTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public RegistrationGateTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static object ValidRequest() => new
    {
        email = $"gate-test-{Guid.NewGuid():N}@example.com",
        password = "S3cure!Passw0rd",
        firstName = "Gate",
        lastName = "Test"
    };

    [Fact]
    public async Task Register_IsForbidden_ByDefault()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", ValidRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invitation", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_Works_WhenExplicitlyEnabled()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Registration:PublicRegistrationEnabled"] = "true"
                });
            });
        }).CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", ValidRequest());

        // Not asserting full success (depends on email infrastructure in tests) -
        // only that the gate no longer blocks the request.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
