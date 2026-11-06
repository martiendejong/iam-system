using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>Task 5632: the service itself enforces the four-eyes review and the tenant rule.</summary>
public class EmergencyOverrideServiceRulesTests
{
    private sealed class FakeAccess : IEmergencyOverrideCallerAccess
    {
        public HashSet<Guid> Managed { get; } = new();
        public Task<bool> CanManageTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Managed.Contains(tenantId));
    }

    private static (EmergencyOverrideService Service, IAMDbContext Context, FakeAccess Access) Create()
    {
        var context = new IAMDbContext(
            new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var access = new FakeAccess();
        return (new EmergencyOverrideService(context, NullLogger<EmergencyOverrideService>.Instance, access), context, access);
    }

    [Fact]
    public async Task Activate_ForUnmanagedTenant_Throws_AndStoresNothing()
    {
        var (service, context, _) = Create();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ActivateOverrideAsync(
            Guid.NewGuid(), Guid.NewGuid(), EmergencyOverrideType.FullAccess, "why", EmergencySeverity.High, 30));

        Assert.Empty(context.EmergencyOverrides);
    }

    [Fact]
    public async Task Activate_ForManagedTenant_Succeeds()
    {
        var (service, _, access) = Create();
        var tenant = Guid.NewGuid();
        access.Managed.Add(tenant);

        var result = await service.ActivateOverrideAsync(
            Guid.NewGuid(), tenant, EmergencyOverrideType.FullAccess, "why", EmergencySeverity.High, 30);

        Assert.Equal(EmergencyOverrideStatus.Active, result.Status);
    }

    [Fact]
    public async Task Review_BySameUser_Throws_AndLeavesOverridePending()
    {
        var (service, context, access) = Create();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        access.Managed.Add(tenant);
        var activated = await service.ActivateOverrideAsync(
            user, tenant, EmergencyOverrideType.FullAccess, "why", EmergencySeverity.High, 30);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ReviewOverrideAsync(activated.Id, user, EmergencyApprovalStatus.Approved, "ok"));

        var stored = await context.EmergencyOverrides.SingleAsync();
        Assert.Equal(EmergencyApprovalStatus.PendingReview, stored.ApprovalStatus);
        Assert.Null(stored.ReviewedByUserId);
    }

    [Fact]
    public async Task Review_ByOtherUser_Succeeds()
    {
        var (service, _, access) = Create();
        var tenant = Guid.NewGuid();
        access.Managed.Add(tenant);
        var activated = await service.ActivateOverrideAsync(
            Guid.NewGuid(), tenant, EmergencyOverrideType.FullAccess, "why", EmergencySeverity.High, 30);
        var reviewer = Guid.NewGuid();

        var reviewed = await service.ReviewOverrideAsync(activated.Id, reviewer, EmergencyApprovalStatus.Approved, "ok");

        Assert.Equal(EmergencyApprovalStatus.Approved, reviewed.ApprovalStatus);
        Assert.Equal(reviewer, reviewed.ReviewedByUserId);
    }
}
