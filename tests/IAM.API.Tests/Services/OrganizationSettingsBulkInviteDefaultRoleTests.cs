using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 4738: a role-less bulk-invite row falls back to the organization's stored default role, but only
/// while that role still passes <see cref="DefaultRoleRules"/> (exists, non-privileged, global or this
/// tenant's). Rows written before the settings API enforced the rule, or straight into the database, are
/// refused per row instead of silently granting an admin role.
/// </summary>
public class OrganizationSettingsBulkInviteDefaultRoleTests
{
    private static readonly Guid InviterId = Guid.NewGuid();

    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IAMDbContext(options);
    }

    private static InvitationService CreateService(IAMDbContext context)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new InvitationService(context, new FakeEmailService(), config, NullLogger<InvitationService>.Instance);
    }

    private static Tenant AddTenant(IAMDbContext context)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        context.Tenants.Add(tenant);
        context.SaveChanges();
        return tenant;
    }

    private static Role AddRole(IAMDbContext context, string name, Guid? tenantId = null, string permissions = "[\"Room.View\"]")
    {
        var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Permissions = permissions };
        context.Roles.Add(role);
        context.SaveChanges();
        return role;
    }

    private static void AddSettings(IAMDbContext context, Guid tenantId, Guid? defaultRoleId)
    {
        context.Set<OrganizationSettings>().Add(new OrganizationSettings
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DefaultRoleId = defaultRoleId
        });
        context.SaveChanges();
    }

    private static List<BulkInviteEntry> RolelessRows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new BulkInviteEntry { Name = $"Person {i}", Email = $"person{i}@bulk.test" })
            .ToList();

    // ----- the stored default role is refused when it breaks the rule ---------------------------

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingManager")]
    [InlineData("OrganizationOwner")]
    [InlineData("Billing Administrator")]
    public async Task RolelessRows_WithPrivilegedStoredDefaultRole_AreRefusedPerRow_AndInviteNothing(string roleName)
    {
        using var context = CreateContext();
        var tenant = AddTenant(context);
        var privileged = AddRole(context, roleName);
        AddSettings(context, tenant.Id, privileged.Id);

        var result = await CreateService(context).SendBulkInvitationsAsync(
            RolelessRows(2), tenant.Id, InviterId, callerIsSuperAdmin: true);

        Assert.Equal(2, result.TotalProcessed);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(2, result.Failed);
        Assert.Equal(new[] { 1, 2 }, result.Errors.Select(e => e.Row).ToArray());
        Assert.All(result.Errors, e => Assert.Contains("privileged", e.Error));
        Assert.Empty(context.Set<Invitation>());
    }

    [Fact]
    public async Task RolelessRows_WithWildcardPermissionStoredDefaultRole_AreRefused()
    {
        using var context = CreateContext();
        var tenant = AddTenant(context);
        var role = AddRole(context, "Operations", permissions: "[\"*\"]");
        AddSettings(context, tenant.Id, role.Id);

        var result = await CreateService(context).SendBulkInvitationsAsync(
            RolelessRows(1), tenant.Id, InviterId, callerIsSuperAdmin: true);

        Assert.Equal(1, result.Failed);
        Assert.Empty(context.Set<Invitation>());
    }

    [Fact]
    public async Task RolelessRows_WithStoredDefaultRoleOfAnotherTenant_AreRefusedPerRow()
    {
        using var context = CreateContext();
        var tenant = AddTenant(context);
        var other = AddTenant(context);
        var foreignRole = AddRole(context, "Member", tenantId: other.Id);
        AddSettings(context, tenant.Id, foreignRole.Id);

        var result = await CreateService(context).SendBulkInvitationsAsync(
            RolelessRows(2), tenant.Id, InviterId, callerIsSuperAdmin: true);

        Assert.Equal(2, result.Failed);
        Assert.All(result.Errors, e => Assert.Contains("another tenant", e.Error));
        Assert.Empty(context.Set<Invitation>());
    }

    [Fact]
    public async Task RolelessRows_WithStoredDefaultRoleThatNoLongerExists_AreRefusedPerRow()
    {
        using var context = CreateContext();
        var tenant = AddTenant(context);
        AddSettings(context, tenant.Id, Guid.NewGuid());

        var result = await CreateService(context).SendBulkInvitationsAsync(
            RolelessRows(1), tenant.Id, InviterId, callerIsSuperAdmin: true);

        Assert.Equal(1, result.Failed);
        Assert.Contains("no longer exists", result.Errors.Single().Error);
        Assert.Empty(context.Set<Invitation>());
    }

    // ----- the rule does not break the working paths --------------------------------------------

    [Fact]
    public async Task RolelessRows_WithValidGlobalStoredDefaultRole_AreInvitedWithThatRole()
    {
        using var context = CreateContext();
        var tenant = AddTenant(context);
        var member = AddRole(context, "Member");
        AddSettings(context, tenant.Id, member.Id);

        var result = await CreateService(context).SendBulkInvitationsAsync(
            RolelessRows(2), tenant.Id, InviterId, callerIsSuperAdmin: false);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.All(context.Set<Invitation>(), i => Assert.Equal(member.Id, i.RoleId));
    }

    [Fact]
    public async Task RolelessRows_WithValidStoredDefaultRoleOwnedByThisTenant_AreInvited()
    {
        using var context = CreateContext();
        var tenant = AddTenant(context);
        var member = AddRole(context, "Tenant Member", tenantId: tenant.Id);
        AddSettings(context, tenant.Id, member.Id);

        var result = await CreateService(context).SendBulkInvitationsAsync(
            RolelessRows(1), tenant.Id, InviterId, callerIsSuperAdmin: false);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(member.Id, context.Set<Invitation>().Single().RoleId);
    }

    [Fact]
    public async Task RowsWithAnExplicitRole_AreNotAffectedByAnInvalidStoredDefaultRole()
    {
        using var context = CreateContext();
        var tenant = AddTenant(context);
        var privileged = AddRole(context, "SuperAdmin");
        var member = AddRole(context, "Member");
        AddSettings(context, tenant.Id, privileged.Id);

        var rows = new List<BulkInviteEntry>
        {
            new() { Email = "named@bulk.test", RoleName = "Member" },
            new() { Email = "roleless@bulk.test" }
        };

        var result = await CreateService(context).SendBulkInvitationsAsync(
            rows, tenant.Id, InviterId, callerIsSuperAdmin: true);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Errors.Single().Row);
        var invitation = context.Set<Invitation>().Single();
        Assert.Equal("named@bulk.test", invitation.Email);
        Assert.Equal(member.Id, invitation.RoleId);
    }

    [Fact]
    public async Task RolelessRows_WithNoStoredDefaultRole_KeepTheExistingError()
    {
        using var context = CreateContext();
        var tenant = AddTenant(context);
        AddSettings(context, tenant.Id, defaultRoleId: null);

        var result = await CreateService(context).SendBulkInvitationsAsync(
            RolelessRows(1), tenant.Id, InviterId, callerIsSuperAdmin: true);

        Assert.Equal(1, result.Failed);
        Assert.Equal("No role specified and no default role configured", result.Errors.Single().Error);
    }

    // ----- the shared rule ----------------------------------------------------------------------

    [Fact]
    public void DefaultRoleRules_Check_ClassifiesEachProblem()
    {
        var tenantId = Guid.NewGuid();

        Assert.Equal(DefaultRoleProblem.NotFound, DefaultRoleRules.Check(null, tenantId));
        Assert.Equal(DefaultRoleProblem.Privileged,
            DefaultRoleRules.Check(new Role { Name = "SystemAdmin" }, tenantId));
        Assert.Equal(DefaultRoleProblem.OtherTenant,
            DefaultRoleRules.Check(new Role { Name = "Member", TenantId = Guid.NewGuid() }, tenantId));
        Assert.Equal(DefaultRoleProblem.None,
            DefaultRoleRules.Check(new Role { Name = "Member" }, tenantId));
        Assert.Equal(DefaultRoleProblem.None,
            DefaultRoleRules.Check(new Role { Name = "Member", TenantId = tenantId }, tenantId));
    }

    [Fact]
    public void DefaultRoleRules_Check_PrivilegeWinsOverTenantMismatch()
    {
        // A privileged role is reported as privileged even when it also belongs to another tenant.
        var role = new Role { Name = "TenantAdmin", TenantId = Guid.NewGuid() };

        Assert.Equal(DefaultRoleProblem.Privileged, DefaultRoleRules.Check(role, Guid.NewGuid()));
    }

    [Fact]
    public void DefaultRoleRules_Check_TenantRoleIsNotUsablePlatformWide()
    {
        var role = new Role { Name = "Member", TenantId = Guid.NewGuid() };

        Assert.Equal(DefaultRoleProblem.OtherTenant, DefaultRoleRules.Check(role, tenantId: null));
    }

    private class FakeEmailService : IEmailService
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
