using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4713: POST /api/portal/link/{provider} used to attach a client-supplied provider user id to the
/// caller's account without any proof of ownership at the provider. It is gone; only the verified
/// code-exchange flow (/api/auth/social/{providerId}/link) may create an external login.
/// </summary>
public class PortalLinkEndpointRemovedTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly IAMTestWebApplicationFactory _factory;

    public PortalLinkEndpointRemovedTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PostPortalLink_WithVictimsProviderId_CreatesNoExternalLogin()
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            db.Users.Add(new User
            {
                Id = userId,
                Email = $"{userId:N}@example.com",
                PasswordHash = "x",
                FirstName = "Att",
                LastName = "Acker",
                EmailConfirmed = true,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var victimProviderId = "victim-" + Guid.NewGuid().ToString("N");
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateJwtToken(userId, "attacker", new[] { "User" }));

        var response = await client.PostAsJsonAsync("/api/portal/link/google",
            new { providerUserId = victimProviderId, email = "victim@example.com", displayName = "Victim" });

        Assert.False(response.IsSuccessStatusCode, $"old link route must not succeed, got {(int)response.StatusCode}");

        using var verify = _factory.Services.CreateScope();
        var ctx = verify.ServiceProvider.GetRequiredService<IAMDbContext>();
        Assert.False(await ctx.ExternalLogins.AnyAsync(el => el.ProviderUserId == victimProviderId));
        Assert.False(await ctx.ExternalLogins.AnyAsync(el => el.UserId == userId));
    }
}
