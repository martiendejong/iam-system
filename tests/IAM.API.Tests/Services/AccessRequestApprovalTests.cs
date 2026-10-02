using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4711: a quorum step counts distinct approvers (one vote row per step and user) and the
/// requester can never approve or deny their own request, whatever roles they hold.
/// </summary>
public class AccessRequestApprovalTests
{
    private static IAMDbContext CreateContext() => new(new DbContextOptionsBuilder<IAMDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AccessRequestService CreateService(IAMDbContext context) =>
        new(context, new NoopEventBus(), new NoopEmail(), NullLogger<AccessRequestService>.Instance);

    private sealed record Fixture(IAMDbContext Context, AccessRequestService Service, Role ApproverRole,
        User Requester, User Alice, User Bob, AccessRequest Request, ApprovalStep Step);

    private static async Task<User> AddUserAsync(IAMDbContext context, string name, Role? role = null)
    {
        var user = new User { Email = $"{name}@example.com", FirstName = name, LastName = "T", PasswordHash = "x", IsActive = true };
        context.Users.Add(user);
        if (role != null) context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<Fixture> CreateFixtureAsync(int quorum, bool requesterHoldsApproverRole = false, bool requesterIsSuperAdmin = false, bool requesterIsNamedApprover = false)
    {
        var context = CreateContext();
        var approverRole = new Role { Name = "Approvers" };
        var superAdmin = new Role { Name = "SuperAdmin" };
        context.Roles.AddRange(approverRole, superAdmin);
        await context.SaveChangesAsync();

        var requester = await AddUserAsync(context, "requester", requesterHoldsApproverRole ? approverRole : null);
        if (requesterIsSuperAdmin)
        {
            context.UserRoles.Add(new UserRole { UserId = requester.Id, RoleId = superAdmin.Id });
            await context.SaveChangesAsync();
        }
        var alice = await AddUserAsync(context, "alice", approverRole);
        var bob = await AddUserAsync(context, "bob", approverRole);

        var request = new AccessRequest { RequesterId = requester.Id, ResourceType = "Role", Justification = "need it" };
        var step = new ApprovalStep
        {
            AccessRequestId = request.Id,
            StepOrder = 1,
            ApproverRoleId = requesterIsNamedApprover ? null : approverRole.Id,
            ApproverId = requesterIsNamedApprover ? requester.Id : null,
            QuorumCount = quorum
        };
        request.ApprovalSteps.Add(step);
        context.AccessRequests.Add(request);
        await context.SaveChangesAsync();
        return new Fixture(context, CreateService(context), approverRole, requester, alice, bob, request, step);
    }

    private static async Task<ApprovalStep> ReloadStepAsync(Fixture f) =>
        await f.Context.ApprovalSteps.AsNoTracking().SingleAsync(s => s.Id == f.Step.Id);

    [Fact]
    public async Task SingleApprover_NormalFlow_ApprovesRequest()
    {
        var f = await CreateFixtureAsync(quorum: 1);

        var result = await f.Service.ApproveAsync(f.Request.Id, f.Alice.Id, "ok");

        Assert.Equal(AccessRequestStatus.Approved, result.Status);
        var step = await ReloadStepAsync(f);
        Assert.Equal(ApprovalStepStatus.Approved, step.Status);
        Assert.Equal(1, step.ApprovalsReceived);
        Assert.Equal(f.Alice.Id, step.DecidedByUserId);
        Assert.Single(await f.Context.ApprovalVotes.ToListAsync());
    }

    [Fact]
    public async Task SameUserApprovingTwice_IsRejected_AndDoesNotRaiseTheCount()
    {
        var f = await CreateFixtureAsync(quorum: 2);

        await f.Service.ApproveAsync(f.Request.Id, f.Alice.Id);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApproveAsync(f.Request.Id, f.Alice.Id));

        Assert.Contains("already approved", ex.Message);
        var step = await ReloadStepAsync(f);
        Assert.Equal(1, step.ApprovalsReceived);
        Assert.Equal(ApprovalStepStatus.Pending, step.Status);
        Assert.Equal(AccessRequestStatus.Pending, (await f.Context.AccessRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await f.Context.ApprovalVotes.ToListAsync());
    }

    [Fact]
    public async Task QuorumOfTwo_CompletesOnlyAfterTwoDifferentApprovers()
    {
        var f = await CreateFixtureAsync(quorum: 2);

        var afterFirst = await f.Service.ApproveAsync(f.Request.Id, f.Alice.Id);
        Assert.Equal(AccessRequestStatus.Pending, afterFirst.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApproveAsync(f.Request.Id, f.Alice.Id));
        Assert.Equal(ApprovalStepStatus.Pending, (await ReloadStepAsync(f)).Status);

        var afterSecond = await f.Service.ApproveAsync(f.Request.Id, f.Bob.Id);

        Assert.Equal(AccessRequestStatus.Approved, afterSecond.Status);
        var step = await ReloadStepAsync(f);
        Assert.Equal(ApprovalStepStatus.Approved, step.Status);
        Assert.Equal(2, step.ApprovalsReceived);
        Assert.Equal(2, await f.Context.ApprovalVotes.CountAsync());
    }

    [Fact]
    public async Task UniqueIndex_OnStepAndUser_IsConfiguredInTheModel()
    {
        var context = CreateContext();
        var index = context.Model.FindEntityType(typeof(ApprovalVote))!
            .GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "ApprovalStepId", "UserId" }));

        Assert.True(index.IsUnique);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, false, false)] // plain requester that is not an approver anyway
    [InlineData(true, false, false)]  // holds the step's approver role
    [InlineData(false, true, false)]  // SuperAdmin
    [InlineData(false, false, true)]  // the step's named approver
    public async Task Requester_CannotApproveOwnRequest(bool holdsRole, bool superAdmin, bool named)
    {
        var f = await CreateFixtureAsync(1, holdsRole, superAdmin, named);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApproveAsync(f.Request.Id, f.Requester.Id));

        Assert.Contains("own access request", ex.Message);
        Assert.Equal(AccessRequestStatus.Pending, (await f.Context.AccessRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, (await ReloadStepAsync(f)).ApprovalsReceived);
        Assert.Empty(await f.Context.ApprovalVotes.ToListAsync());
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Requester_CannotDenyOwnRequest(bool holdsRole, bool superAdmin, bool named)
    {
        var f = await CreateFixtureAsync(1, holdsRole, superAdmin, named);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DenyAsync(f.Request.Id, f.Requester.Id, "no"));

        Assert.Contains("own access request", ex.Message);
        Assert.Equal(AccessRequestStatus.Pending, (await f.Context.AccessRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(ApprovalStepStatus.Pending, (await ReloadStepAsync(f)).Status);
    }

    [Fact]
    public async Task OtherApproverCanStillDeny()
    {
        var f = await CreateFixtureAsync(quorum: 2);

        var result = await f.Service.DenyAsync(f.Request.Id, f.Alice.Id, "no");

        Assert.Equal(AccessRequestStatus.Denied, result.Status);
    }

    [Fact]
    public async Task InFlightStep_KeepsItsOldCounter_AndStillCountsTowardQuorum()
    {
        // A step created before votes existed: one earlier approval, only the counter and
        // DecidedByUserId remember it.
        var f = await CreateFixtureAsync(quorum: 2);
        var step = await f.Context.ApprovalSteps.SingleAsync();
        step.ApprovalsReceived = 1;
        step.DecidedByUserId = f.Alice.Id;
        await f.Context.SaveChangesAsync();

        // the earlier approver cannot be counted a second time
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApproveAsync(f.Request.Id, f.Alice.Id));
        Assert.Equal(1, (await ReloadStepAsync(f)).ApprovalsReceived);

        // one new distinct approver completes the quorum on top of the preserved count
        var result = await f.Service.ApproveAsync(f.Request.Id, f.Bob.Id);

        Assert.Equal(AccessRequestStatus.Approved, result.Status);
        Assert.Equal(2, (await ReloadStepAsync(f)).ApprovalsReceived);
    }

    [Fact]
    public async Task Audit_RecordsApproverAndRunningCount()
    {
        var f = await CreateFixtureAsync(quorum: 2);

        await f.Service.ApproveAsync(f.Request.Id, f.Alice.Id, "first");
        await f.Service.ApproveAsync(f.Request.Id, f.Bob.Id, "second");

        var audits = (await f.Context.AuditLogs.Where(a => a.Action == "AccessRequestStepApproved").ToListAsync())
            .OrderBy(a => a.CreatedAt).ToList();
        Assert.Equal(2, audits.Count);
        Assert.Equal(new[] { f.Alice.Id, f.Bob.Id }.OrderBy(x => x), audits.Select(a => a.UserId!.Value).OrderBy(x => x));
        var counts = audits.Select(a => JsonDocument.Parse(a.Details!).RootElement.GetProperty("approvalsReceived").GetInt32()).OrderBy(x => x);
        Assert.Equal(new[] { 1, 2 }, counts);
    }

    private sealed class NoopEventBus : IEventBus
    {
        public Task PublishAsync(string eventType, object payload, Guid? tenantId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<WebhookDelivery>> GetDeliveryHistoryAsync(Guid subscriptionId, int limit = 50, CancellationToken ct = default) => Task.FromResult(new List<WebhookDelivery>());
    }

    private sealed class NoopEmail : IEmailService
    {
        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }
}
