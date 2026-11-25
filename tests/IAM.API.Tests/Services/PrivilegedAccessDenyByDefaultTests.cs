using System.Text.Json;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5629: PAM is deny-by-default. Checkout and break-glass need an active policy for the role and tenant and an
/// eligible caller; break-glass stays pending until every named approver has confirmed it themselves.
/// </summary>
public class PrivilegedAccessDenyByDefaultTests
{
    private sealed class CapturingLogger : ILogger<PrivilegedAccessService>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class Fixture
    {
        public IAMDbContext Context { get; } =
            new(new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public CapturingLogger Logger { get; } = new();
        public PrivilegedAccessService Service { get; }
        public Tenant Tenant { get; } = new() { Name = "T1", Slug = "t1" };
        public Tenant OtherTenant { get; } = new() { Name = "T2", Slug = "t2" };
        public Role Privileged { get; } = new() { Name = "SuperAdmin" };
        public Role Eligible { get; } = new() { Name = "OnCall" };
        public Role Admin { get; } = new() { Name = "SecurityAdmin" };

        public Fixture()
        {
            Service = new PrivilegedAccessService(Context, Logger);
            Context.Tenants.AddRange(Tenant, OtherTenant);
            Context.Roles.AddRange(Privileged, Eligible, Admin);
            Context.SaveChanges();
        }

        public User AddUser(string name, bool active = true)
        {
            var user = new User
            {
                Email = $"{name}@example.com", PasswordHash = "x", FirstName = name, LastName = "T",
                EmailConfirmed = true, IsActive = active
            };
            Context.Users.Add(user);
            Context.SaveChanges();
            return user;
        }

        public void Grant(User user, Role role, Guid? tenantId, DateTime? expiresAt = null)
        {
            Context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, TenantId = tenantId, ExpiresAt = expiresAt });
            Context.SaveChanges();
        }

        public PamPolicy AddPolicy(Guid? eligibleRoleId, bool requireApproval = false, bool breakGlass = true,
            Guid? tenantId = null, Guid? approverRoleId = null)
        {
            var policy = new PamPolicy
            {
                TenantId = tenantId ?? Tenant.Id, RoleId = Privileged.Id, EligibleRoleId = eligibleRoleId,
                RequireApproval = requireApproval, BreakGlassEnabled = breakGlass, ApproverRoleId = approverRoleId,
                MaxDurationMinutes = 120
            };
            Context.PamPolicies.Add(policy);
            Context.SaveChanges();
            return policy;
        }

        public User EligibleUser(string name = "alice")
        {
            var user = AddUser(name);
            Grant(user, Eligible, Tenant.Id);
            return user;
        }

        public User AdminUser(string name)
        {
            var user = AddUser(name);
            Grant(user, Admin, Tenant.Id);
            return user;
        }
    }

    // --- Checkout ---

    [Fact]
    public async Task Checkout_without_policy_is_refused_and_creates_no_session()
    {
        var f = new Fixture();
        var user = f.EligibleUser();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.CheckoutAsync(user.Id, f.Privileged.Id, f.Tenant.Id, "why"));

        Assert.Empty(f.Context.PrivilegedSessions);
        Assert.Contains(f.Logger.Messages, m => m.Contains("PAM REFUSED") && m.Contains("pam-"));
    }

    [Fact]
    public async Task Checkout_with_inactive_policy_is_refused()
    {
        var f = new Fixture();
        var user = f.EligibleUser();
        var policy = f.AddPolicy(f.Eligible.Id);
        policy.IsActive = false;
        f.Context.SaveChanges();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.CheckoutAsync(user.Id, f.Privileged.Id, f.Tenant.Id, "why"));
    }

    [Fact]
    public async Task Checkout_by_plain_user_without_eligible_role_is_refused()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var plain = f.AddUser("plain");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.CheckoutAsync(plain.Id, f.Privileged.Id, f.Tenant.Id, "why"));
        Assert.Empty(f.Context.PrivilegedSessions);
    }

    [Fact]
    public async Task Checkout_with_policy_that_defines_no_eligible_role_is_refused_for_everyone()
    {
        var f = new Fixture();
        f.AddPolicy(eligibleRoleId: null);
        var user = f.EligibleUser();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.CheckoutAsync(user.Id, f.Privileged.Id, f.Tenant.Id, "why"));
    }

    [Fact]
    public async Task Checkout_by_caller_eligible_only_in_another_tenant_is_refused()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var user = f.AddUser("bob");
        f.Grant(user, f.Eligible, f.OtherTenant.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.CheckoutAsync(user.Id, f.Privileged.Id, f.Tenant.Id, "why"));
    }

    [Fact]
    public async Task Checkout_with_expired_eligibility_is_refused()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var user = f.AddUser("carol");
        f.Grant(user, f.Eligible, f.Tenant.Id, DateTime.UtcNow.AddMinutes(-1));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.CheckoutAsync(user.Id, f.Privileged.Id, f.Tenant.Id, "why"));
    }

    [Fact]
    public async Task Checkout_by_eligible_caller_with_policy_creates_active_session()
    {
        var f = new Fixture();
        var policy = f.AddPolicy(f.Eligible.Id);
        var user = f.EligibleUser();

        var session = await f.Service.CheckoutAsync(user.Id, f.Privileged.Id, f.Tenant.Id, "why", 30);

        Assert.Equal(PrivilegedSessionStatus.Active, session.Status);
        Assert.Equal(policy.Id, session.PamPolicyId);
    }

    [Fact]
    public async Task Checkout_when_policy_requires_approval_stays_pending_and_approval_needs_tenant_admin()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id, requireApproval: true);
        var user = f.EligibleUser();
        var session = await f.Service.CheckoutAsync(user.Id, f.Privileged.Id, f.Tenant.Id, "why");
        Assert.Equal(PrivilegedSessionStatus.Pending, session.Status);

        var foreignAdmin = f.AddUser("foreign");
        f.Grant(foreignAdmin, f.Admin, f.OtherTenant.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.ApproveSessionAsync(session.Id, foreignAdmin.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.DenySessionAsync(session.Id, foreignAdmin.Id));

        var admin = f.AdminUser("admin");
        var approved = await f.Service.ApproveSessionAsync(session.Id, admin.Id);
        Assert.Equal(PrivilegedSessionStatus.Active, approved.Status);
    }

    // --- Break-glass ---

    [Fact]
    public async Task BreakGlass_without_policy_is_refused()
    {
        var f = new Fixture();
        var user = f.EligibleUser();
        var a1 = f.AdminUser("a1");
        var a2 = f.AdminUser("a2");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.BreakGlassAsync(user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id }));
        Assert.Empty(f.Context.PrivilegedSessions);
    }

    [Fact]
    public async Task BreakGlass_by_ineligible_caller_is_refused()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var plain = f.AddUser("plain");
        var a1 = f.AdminUser("a1");
        var a2 = f.AdminUser("a2");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Service.BreakGlassAsync(plain.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id }));
    }

    [Fact]
    public async Task BreakGlass_is_pending_until_every_named_approver_confirms()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var user = f.EligibleUser();
        var a1 = f.AdminUser("a1");
        var a2 = f.AdminUser("a2");

        var session = await f.Service.BreakGlassAsync(
            user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id }, 30);
        Assert.Equal(PrivilegedSessionStatus.Pending, session.Status);
        Assert.Null(session.CheckedOutAt);
        Assert.False(session.IsActive());

        var afterOne = await f.Service.ConfirmBreakGlassAsync(session.Id, a1.Id);
        Assert.Equal(PrivilegedSessionStatus.Pending, afterOne.Status);

        var afterTwo = await f.Service.ConfirmBreakGlassAsync(session.Id, a2.Id);
        Assert.Equal(PrivilegedSessionStatus.Active, afterTwo.Status);
        Assert.NotNull(afterTwo.CheckedOutAt);
        Assert.True(afterTwo.ExpiresAt > DateTime.UtcNow.AddMinutes(25));
        Assert.Equal(2, JsonSerializer.Deserialize<List<Guid>>(afterTwo.BreakGlassConfirmations!)!.Count);
        Assert.Contains(f.Logger.Messages, m => m.Contains("BREAK-GLASS CONFIRMED") && m.Contains(session.AuditCorrelationId!));
    }

    [Fact]
    public async Task BreakGlass_approver_cannot_confirm_twice_and_double_confirm_does_not_activate()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var user = f.EligibleUser();
        var a1 = f.AdminUser("a1");
        var a2 = f.AdminUser("a2");
        var session = await f.Service.BreakGlassAsync(
            user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id });

        await f.Service.ConfirmBreakGlassAsync(session.Id, a1.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmBreakGlassAsync(session.Id, a1.Id));

        var reloaded = await f.Context.PrivilegedSessions.FindAsync(session.Id);
        Assert.Equal(PrivilegedSessionStatus.Pending, reloaded!.Status);
        Assert.Contains(f.Logger.Messages, m => m.Contains("CONFIRMATION REFUSED") && m.Contains(session.AuditCorrelationId!));
    }

    [Fact]
    public async Task BreakGlass_requester_and_strangers_cannot_confirm()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var user = f.EligibleUser();
        var a1 = f.AdminUser("a1");
        var a2 = f.AdminUser("a2");
        var stranger = f.AdminUser("stranger");
        var session = await f.Service.BreakGlassAsync(
            user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id });

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmBreakGlassAsync(session.Id, user.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmBreakGlassAsync(session.Id, stranger.Id));
    }

    [Fact]
    public async Task BreakGlass_named_approver_without_approver_rights_cannot_confirm()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var user = f.EligibleUser();
        var a1 = f.AdminUser("a1");
        var powerless = f.AddUser("powerless");
        var session = await f.Service.BreakGlassAsync(
            user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, powerless.Id });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ConfirmBreakGlassAsync(session.Id, powerless.Id));
    }

    [Fact]
    public async Task BreakGlass_policy_approver_role_grants_approver_rights()
    {
        var f = new Fixture();
        var approverRole = new Role { Name = "PamApprover" };
        f.Context.Roles.Add(approverRole);
        f.Context.SaveChanges();
        f.AddPolicy(f.Eligible.Id, approverRoleId: approverRole.Id);
        var user = f.EligibleUser();
        var a1 = f.AddUser("a1");
        var a2 = f.AddUser("a2");
        f.Grant(a1, approverRole, f.Tenant.Id);
        f.Grant(a2, approverRole, f.Tenant.Id);
        var session = await f.Service.BreakGlassAsync(
            user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id });

        await f.Service.ConfirmBreakGlassAsync(session.Id, a1.Id);
        var result = await f.Service.ConfirmBreakGlassAsync(session.Id, a2.Id);
        Assert.Equal(PrivilegedSessionStatus.Active, result.Status);
    }

    [Fact]
    public async Task BreakGlass_session_cannot_be_activated_through_normal_approval()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var user = f.EligibleUser();
        var a1 = f.AdminUser("a1");
        var a2 = f.AdminUser("a2");
        var session = await f.Service.BreakGlassAsync(
            user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id });

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApproveSessionAsync(session.Id, a1.Id));
        var reloaded = await f.Context.PrivilegedSessions.FindAsync(session.Id);
        Assert.Equal(PrivilegedSessionStatus.Pending, reloaded!.Status);
    }

    [Fact]
    public async Task BreakGlass_unconfirmed_request_expires_and_can_no_longer_be_confirmed()
    {
        var f = new Fixture();
        f.AddPolicy(f.Eligible.Id);
        var user = f.EligibleUser();
        var a1 = f.AdminUser("a1");
        var a2 = f.AdminUser("a2");
        var session = await f.Service.BreakGlassAsync(
            user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id }, 30);

        session.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        f.Context.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmBreakGlassAsync(session.Id, a1.Id));
        var reloaded = await f.Context.PrivilegedSessions.FindAsync(session.Id);
        Assert.Equal(PrivilegedSessionStatus.Expired, reloaded!.Status);

        // the sweeper also expires pending requests that nobody touched
        var other = await f.Service.BreakGlassAsync(
            user.Id, f.Privileged.Id, f.Tenant.Id, "fire", new() { a1.Id, a2.Id }, 30);
        other.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        f.Context.SaveChanges();
        await f.Service.ProcessExpiredSessionsAsync();
        Assert.Equal(PrivilegedSessionStatus.Expired, (await f.Context.PrivilegedSessions.FindAsync(other.Id))!.Status);
        Assert.DoesNotContain(f.Context.PrivilegedSessions, s => s.Status == PrivilegedSessionStatus.Active);
    }
}
