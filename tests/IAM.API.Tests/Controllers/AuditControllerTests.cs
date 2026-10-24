using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Integration tests for AuditController
/// Tests all 5 endpoints with various scenarios
/// </summary>
public class AuditControllerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly IAMTestWebApplicationFactory _factory;

    public AuditControllerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateAdminToken());
    }

    [Fact]
    public async Task GetAuditEvents_ReturnsSuccess()
    {
        // Act
        var response = await _client.GetAsync("/api/audit/events");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<PolicyAuditEvent>>();
        Assert.NotNull(events);
    }

    [Fact]
    public async Task GetAuditEvents_WithFiltering_ReturnsFilteredResults()
    {
        // Arrange
        var startDate = DateTime.UtcNow.AddDays(-7).ToString("O");
        var endDate = DateTime.UtcNow.ToString("O");

        // Act
        var response = await _client.GetAsync($"/api/audit/events?startDate={startDate}&endDate={endDate}&take=50");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<PolicyAuditEvent>>();
        Assert.NotNull(events);
    }

    [Fact]
    public async Task GetComplianceStatistics_ReturnsStatistics()
    {
        // Arrange
        var startDate = DateTime.UtcNow.AddDays(-30).ToString("O");
        var endDate = DateTime.UtcNow.ToString("O");

        // Act
        var response = await _client.GetAsync($"/api/audit/statistics?startDate={startDate}&endDate={endDate}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stats = await response.Content.ReadFromJsonAsync<ComplianceStatistics>();
        Assert.NotNull(stats);
        Assert.True(stats.TotalPolicies >= 0);
    }

    [Fact]
    public async Task GetComplianceStatistics_WithoutDates_ReturnsBadRequest()
    {
        // Act
        var response = await _client.GetAsync("/api/audit/statistics");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GenerateComplianceReport_CreatesReport()
    {
        // Arrange
        var request = new
        {
            Framework = "SOC2",
            PeriodStart = DateTime.UtcNow.AddDays(-30),
            PeriodEnd = DateTime.UtcNow,
            TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111")
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/audit/reports", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ComplianceReport>();
        Assert.NotNull(report);
        Assert.Equal("SOC2", report.Framework);
        Assert.True(report.Score >= 0 && report.Score <= 100);
    }

    [Fact]
    public async Task GenerateComplianceReport_WithoutFramework_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            Framework = "",
            PeriodStart = DateTime.UtcNow.AddDays(-30),
            PeriodEnd = DateTime.UtcNow
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/audit/reports", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DetectAnomalies_ReturnsAnomalies()
    {
        // Act
        var response = await _client.GetAsync("/api/audit/anomalies");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var anomalies = await response.Content.ReadFromJsonAsync<List<PolicyAuditEvent>>();
        Assert.NotNull(anomalies);
    }

    [Fact]
    public async Task DetectAnomalies_WithSinceParameter_ReturnsFilteredAnomalies()
    {
        // Arrange
        var since = DateTime.UtcNow.AddDays(-1).ToString("O");

        // Act
        var response = await _client.GetAsync($"/api/audit/anomalies?since={since}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DetectAnomalies_RequiresSecurityAdminRole()
    {
        // Arrange
        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        // Act
        var response = await userClient.GetAsync("/api/audit/anomalies");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CleanupOldAuditEvents_DeletesEvents()
    {
        // Act
        var response = await _client.DeleteAsync("/api/audit/cleanup?retentionDays=90");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Dictionary<string, int>>();
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("deletedCount"));
    }

    [Fact]
    public async Task CleanupOldAuditEvents_InvalidRetention_ReturnsBadRequest()
    {
        // Act - Too short retention
        var response1 = await _client.DeleteAsync("/api/audit/cleanup?retentionDays=15");

        // Act - Too long retention
        var response2 = await _client.DeleteAsync("/api/audit/cleanup?retentionDays=4000");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response1.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);
    }

    [Fact]
    public async Task CleanupOldAuditEvents_RequiresSystemAdminRole()
    {
        // Arrange
        var userClient = _factory.CreateClient();
        userClient.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        // Act
        var response = await userClient.DeleteAsync("/api/audit/cleanup?retentionDays=90");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // Task 4714: events / statistics / reports are admin-only and tenant-scoped.
    // Default token shape below = password login: roles but NO tenant_id claim, so a caller's
    // tenant authority has to come from an active UserRoles row, not from a claim.
    // ---------------------------------------------------------------------------------------

    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed record SeededTenant(Guid TenantId, Guid AuditedUserId, Guid PolicyId);

    /// <summary>Tenant with 2 allowed + 1 denied evaluation events, each tied to one user/policy id.</summary>
    private async Task<SeededTenant> SeedTenantWithAuditEventsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        var auditedUserId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        db.Tenants.Add(tenant);

        foreach (var allowed in new[] { true, true, false })
        {
            db.PolicyAuditEvents.Add(new PolicyAuditEvent
            {
                EventType = PolicyAuditEventType.AccessEvaluated,
                TenantId = tenant.Id,
                UserId = auditedUserId,
                PolicyId = policyId,
                Resource = $"resource-{tenant.Id:N}",
                WasAllowed = allowed,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            });
        }

        await db.SaveChangesAsync();
        return new SeededTenant(tenant.Id, auditedUserId, policyId);
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@audit-scope.test",
            PasswordHash = "not-used"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task GrantRoleAsync(Guid userId, string roleName, Guid? tenantId, DateTime? expiresAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName, TenantId = RootTenantId };
            db.Roles.Add(role);
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            TenantId = tenantId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
    }

    private HttpClient ClientAs(Guid userId, string[] roles, string? tenantClaim = null)
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(
            TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@audit-scope.test", roles, tenantClaim));
        return client;
    }

    private async Task<HttpClient> TenantAdminClientAsync(Guid tenantId, string roleName = "TenantAdmin")
    {
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, roleName, tenantId);
        return ClientAs(userId, new[] { roleName });
    }

    public static IEnumerable<object[]> AuditEndpoints() => new[]
    {
        new object[] { "events" },
        new object[] { "statistics" },
        new object[] { "reports" }
    };

    /// <summary>Calls one of the three audit endpoints, optionally naming a tenant.</summary>
    private static Task<HttpResponseMessage> CallAuditEndpointAsync(HttpClient client, string endpoint, Guid? tenantId)
    {
        var tenantQuery = tenantId.HasValue ? $"&tenantId={tenantId.Value}" : string.Empty;
        switch (endpoint)
        {
            case "events":
                return client.GetAsync($"/api/audit/events?take=1000{tenantQuery}");
            case "statistics":
                var start = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("O"));
                var end = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
                return client.GetAsync($"/api/audit/statistics?startDate={start}&endDate={end}{tenantQuery}");
            case "reports":
                return client.PostAsJsonAsync("/api/audit/reports", new
                {
                    Framework = "SOC2",
                    PeriodStart = DateTime.UtcNow.AddDays(-30),
                    PeriodEnd = DateTime.UtcNow.AddDays(1),
                    TenantId = tenantId
                });
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null);
        }
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_OrdinaryUser_ReturnsForbidden(string endpoint)
    {
        var tenant = await SeedTenantWithAuditEventsAsync();
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "User", tenant.TenantId);
        var client = ClientAs(userId, new[] { "User" });

        var withoutTenant = await CallAuditEndpointAsync(client, endpoint, null);
        var ownTenant = await CallAuditEndpointAsync(client, endpoint, tenant.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, withoutTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ownTenant.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_StockUserToken_ReturnsForbidden(string endpoint)
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(TestAuthenticationHelper.GenerateUserToken());

        var response = await CallAuditEndpointAsync(client, endpoint, RootTenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_AdminRoleClaimWithoutTenantMembership_ReturnsForbidden(string endpoint)
    {
        // The role claim alone is global and forgeable by whoever issued it; membership is the UserRoles row.
        var tenant = await SeedTenantWithAuditEventsAsync();
        var client = ClientAs(await CreateUserAsync(), new[] { "TenantAdmin" });

        var response = await CallAuditEndpointAsync(client, endpoint, tenant.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_ServiceAccountToken_ReturnsForbidden(string endpoint)
    {
        var client = _factory.CreateClient();
        client.AddAuthorizationHeader(
            TestAuthenticationHelper.GenerateServiceAccountToken("audit-reader", "audit:read", "users:create"));

        var response = await CallAuditEndpointAsync(client, endpoint, RootTenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingOwner")]
    [InlineData("BuildingManager")]
    public async Task GetAuditEvents_TenantAdmin_SeesOnlyOwnTenantWhenNoTenantGiven(string adminRole)
    {
        var own = await SeedTenantWithAuditEventsAsync();
        var foreign = await SeedTenantWithAuditEventsAsync();
        var client = await TenantAdminClientAsync(own.TenantId, adminRole);

        var response = await client.GetAsync("/api/audit/events?take=1000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<PolicyAuditEvent>>();
        Assert.NotNull(events);
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(own.TenantId, e.TenantId));
        Assert.DoesNotContain(events, e => e.TenantId == foreign.TenantId);
    }

    [Fact]
    public async Task GetAuditEvents_TenantAdmin_OwnTenantIdInQuery_ReturnsOwnEvents()
    {
        var own = await SeedTenantWithAuditEventsAsync();
        var client = await TenantAdminClientAsync(own.TenantId);

        var response = await client.GetAsync($"/api/audit/events?tenantId={own.TenantId}&take=1000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<PolicyAuditEvent>>();
        Assert.NotNull(events);
        Assert.Equal(3, events.Count);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_TenantAdmin_ForeignTenantId_ReturnsForbidden(string endpoint)
    {
        var own = await SeedTenantWithAuditEventsAsync();
        var foreign = await SeedTenantWithAuditEventsAsync();
        var client = await TenantAdminClientAsync(own.TenantId);

        var response = await CallAuditEndpointAsync(client, endpoint, foreign.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAuditEvents_TenantAdmin_ForeignUserOrPolicyFilter_CannotReachOtherTenantRows()
    {
        var own = await SeedTenantWithAuditEventsAsync();
        var foreign = await SeedTenantWithAuditEventsAsync();
        var client = await TenantAdminClientAsync(own.TenantId);

        // Filters stay ANDed with the pinned tenant, so another tenant's user/policy ids match nothing.
        var byUser = await client.GetFromJsonAsync<List<PolicyAuditEvent>>($"/api/audit/events?userId={foreign.AuditedUserId}&take=1000");
        var byPolicy = await client.GetFromJsonAsync<List<PolicyAuditEvent>>($"/api/audit/events?policyId={foreign.PolicyId}&take=1000");

        Assert.NotNull(byUser);
        Assert.Empty(byUser);
        Assert.NotNull(byPolicy);
        Assert.Empty(byPolicy);
    }

    [Fact]
    public async Task GetComplianceStatistics_TenantAdmin_IsPinnedToOwnTenant()
    {
        var own = await SeedTenantWithAuditEventsAsync();
        await SeedTenantWithAuditEventsAsync();
        var client = await TenantAdminClientAsync(own.TenantId);

        var response = await CallAuditEndpointAsync(client, "statistics", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stats = await response.Content.ReadFromJsonAsync<ComplianceStatistics>();
        Assert.NotNull(stats);
        Assert.Equal(3, stats.TotalEvaluations);
        Assert.Equal(2, stats.AccessGranted);
        Assert.Equal(1, stats.AccessDenied);
    }

    [Fact]
    public async Task GenerateComplianceReport_TenantAdmin_OwnTenant_ReturnsReport()
    {
        var own = await SeedTenantWithAuditEventsAsync();
        var client = await TenantAdminClientAsync(own.TenantId);

        var response = await CallAuditEndpointAsync(client, "reports", own.TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ComplianceReport>();
        Assert.NotNull(report);
        Assert.Equal(own.TenantId, report.TenantId);
    }

    [Fact]
    public async Task GenerateComplianceReport_TenantAdmin_NoTenantInBody_IsPinnedToOwnTenant()
    {
        var own = await SeedTenantWithAuditEventsAsync();
        var client = await TenantAdminClientAsync(own.TenantId);

        var response = await CallAuditEndpointAsync(client, "reports", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ComplianceReport>();
        Assert.NotNull(report);
        Assert.Equal(own.TenantId, report.TenantId);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_TenantAdminOfSeveralTenants_MustNameATenant(string endpoint)
    {
        var first = await SeedTenantWithAuditEventsAsync();
        var second = await SeedTenantWithAuditEventsAsync();
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", first.TenantId);
        await GrantRoleAsync(userId, "TenantAdmin", second.TenantId);
        var client = ClientAs(userId, new[] { "TenantAdmin" });

        var withoutTenant = await CallAuditEndpointAsync(client, endpoint, null);
        var named = await CallAuditEndpointAsync(client, endpoint, second.TenantId);

        Assert.Equal(HttpStatusCode.BadRequest, withoutTenant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_TenantScopedToken_OnlyHoldsAuthorityInItsTenant(string endpoint)
    {
        var first = await SeedTenantWithAuditEventsAsync();
        var second = await SeedTenantWithAuditEventsAsync();
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", first.TenantId);
        await GrantRoleAsync(userId, "TenantAdmin", second.TenantId);
        var client = ClientAs(userId, new[] { "TenantAdmin" }, tenantClaim: first.TenantId.ToString());

        // The tenant_id claim narrows the two memberships to one: no ambiguity, other tenant refused.
        var withoutTenant = await CallAuditEndpointAsync(client, endpoint, null);
        var otherTenant = await CallAuditEndpointAsync(client, endpoint, second.TenantId);

        Assert.Equal(HttpStatusCode.OK, withoutTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_TenantScopedTokenForAnotherTenant_ReturnsForbidden(string endpoint)
    {
        var own = await SeedTenantWithAuditEventsAsync();
        var foreign = await SeedTenantWithAuditEventsAsync();
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", own.TenantId);
        var client = ClientAs(userId, new[] { "TenantAdmin" }, tenantClaim: foreign.TenantId.ToString());

        var response = await CallAuditEndpointAsync(client, endpoint, foreign.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_ExpiredTenantAdminRole_ReturnsForbidden(string endpoint)
    {
        var tenant = await SeedTenantWithAuditEventsAsync();
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", tenant.TenantId, expiresAt: DateTime.UtcNow.AddDays(-1));
        var client = ClientAs(userId, new[] { "TenantAdmin" });

        var response = await CallAuditEndpointAsync(client, endpoint, tenant.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AuditEndpoints))]
    public async Task AuditEndpoints_GlobalAdminRoleRowWithoutTenant_ReturnsForbidden(string endpoint)
    {
        // A role row with no tenant is not a tenant-admin grant for any particular tenant.
        var tenant = await SeedTenantWithAuditEventsAsync();
        var userId = await CreateUserAsync();
        await GrantRoleAsync(userId, "TenantAdmin", tenantId: null);
        var client = ClientAs(userId, new[] { "TenantAdmin" });

        var response = await CallAuditEndpointAsync(client, endpoint, tenant.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SecurityAdmin")]
    public async Task GetAuditEvents_PlatformAdmin_CanQueryAnyTenantAndAllTenants(string platformRole)
    {
        var first = await SeedTenantWithAuditEventsAsync();
        var second = await SeedTenantWithAuditEventsAsync();
        var client = ClientAs(await CreateUserAsync(), new[] { platformRole });

        var firstOnly = await client.GetFromJsonAsync<List<PolicyAuditEvent>>($"/api/audit/events?tenantId={first.TenantId}&take=1000");
        var secondOnly = await client.GetFromJsonAsync<List<PolicyAuditEvent>>($"/api/audit/events?tenantId={second.TenantId}&take=1000");
        var all = await client.GetFromJsonAsync<List<PolicyAuditEvent>>("/api/audit/events?take=1000");

        Assert.NotNull(firstOnly);
        Assert.Equal(3, firstOnly.Count);
        Assert.All(firstOnly, e => Assert.Equal(first.TenantId, e.TenantId));
        Assert.NotNull(secondOnly);
        Assert.Equal(3, secondOnly.Count);
        Assert.NotNull(all);
        Assert.Contains(all, e => e.TenantId == first.TenantId);
        Assert.Contains(all, e => e.TenantId == second.TenantId);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SecurityAdmin")]
    public async Task StatisticsAndReports_PlatformAdmin_CanTargetAnyTenantOrAllTenants(string platformRole)
    {
        var tenant = await SeedTenantWithAuditEventsAsync();
        var client = ClientAs(await CreateUserAsync(), new[] { platformRole });

        var statsForTenant = await CallAuditEndpointAsync(client, "statistics", tenant.TenantId);
        var statsAll = await CallAuditEndpointAsync(client, "statistics", null);
        var reportForTenant = await CallAuditEndpointAsync(client, "reports", tenant.TenantId);
        var reportAll = await CallAuditEndpointAsync(client, "reports", null);

        Assert.Equal(HttpStatusCode.OK, statsForTenant.StatusCode);
        Assert.Equal(3, (await statsForTenant.Content.ReadFromJsonAsync<ComplianceStatistics>())!.TotalEvaluations);
        Assert.Equal(HttpStatusCode.OK, statsAll.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reportForTenant.StatusCode);
        Assert.Equal(tenant.TenantId, (await reportForTenant.Content.ReadFromJsonAsync<ComplianceReport>())!.TenantId);
        Assert.Equal(HttpStatusCode.OK, reportAll.StatusCode);
        Assert.Null((await reportAll.Content.ReadFromJsonAsync<ComplianceReport>())!.TenantId);
    }
}
