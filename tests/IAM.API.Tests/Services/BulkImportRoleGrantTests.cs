using System.Security.Claims;
using System.Text;
using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>
/// Task 5146: the bulk user import applies the same role-grant rules as invitations. Only a SuperAdmin may import a
/// user with a platform-wide role; a refused row is a row error (dry run too) and no user or role row is created.
/// </summary>
public class BulkImportRoleGrantTests
{
    private static readonly Guid CallerId = Guid.NewGuid();

    private static IAMDbContext CreateContext() => new(
        new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Role AddRole(IAMDbContext context, string name, Guid? tenantId = null)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Permissions = "[\"Room.View\"]" };
        context.Roles.Add(role);
        context.SaveChanges();
        return role;
    }

    private static MemoryStream Csv(string role, string email = "new.user@example.com") =>
        new(Encoding.UTF8.GetBytes($"email,firstname,lastname,role\n{email},New,User,{role}\n"));

    private static Task<BulkOperation> Import(IAMDbContext context, TenantGrantor grantor, string role, bool dryRun, Guid tenantId) =>
        new BulkOperationService(context).ImportUsersAsync(
            tenantId, CallerId, Csv(role), "users.csv", BulkOperationFormat.CSV, grantor, dryRun);

    private static readonly TenantGrantor SystemAdmin = new(false, true);

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("superadmin")]
    [InlineData("  SuperADMIN  ")]
    [InlineData("SystemAdmin")]
    [InlineData("SecurityAdmin")]
    [InlineData("ComplianceOfficer")]
    [InlineData("EmergencyAccess")]
    [InlineData("Admin")]
    public async Task SystemAdmin_ImportingAPlatformRole_CreatesNoUserAndNoRole_AndReportsTheRowError(string roleName)
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        AddRole(context, "SuperAdmin");
        AddRole(context, "SystemAdmin");
        AddRole(context, "SecurityAdmin");
        AddRole(context, "ComplianceOfficer");
        AddRole(context, "EmergencyAccess");
        AddRole(context, "Admin");

        var operation = await Import(context, SystemAdmin, roleName, dryRun: false, tenantId);

        Assert.Empty(context.Users);
        Assert.Empty(context.UserRoles);
        Assert.Equal(1, operation.ErrorRows);
        Assert.Equal(0, operation.SuccessRows);
        Assert.Contains("can only be granted by a SuperAdmin", operation.ErrorDetails);
    }

    [Fact]
    public async Task SystemAdmin_DryRun_ReportsTheSameRowError()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        AddRole(context, "SuperAdmin");

        var dry = await Import(context, SystemAdmin, "SuperAdmin", dryRun: true, tenantId);
        var real = await Import(context, SystemAdmin, "SuperAdmin", dryRun: false, tenantId);

        Assert.Equal(1, dry.ErrorRows);
        Assert.Equal(0, dry.SuccessRows);
        Assert.Contains("can only be granted by a SuperAdmin", dry.ErrorDetails);
        Assert.Equal(dry.ErrorDetails, real.ErrorDetails);
        Assert.Empty(context.Users);
    }

    [Fact]
    public async Task SuperAdmin_ImportingAPlatformRole_StillSucceeds_AndDryRunIsClean()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var role = AddRole(context, "SuperAdmin");

        var dry = await Import(context, TenantGrantor.SuperAdmin, "SuperAdmin", dryRun: true, tenantId);
        Assert.Equal(0, dry.ErrorRows);
        Assert.Equal(1, dry.SuccessRows);
        Assert.Empty(context.Users);

        var real = await Import(context, TenantGrantor.SuperAdmin, "SuperAdmin", dryRun: false, tenantId);

        Assert.Equal(1, real.SuccessRows);
        Assert.Equal(0, real.ErrorRows);
        Assert.Single(context.Users);
        Assert.Equal(role.Id, Assert.Single(context.UserRoles).RoleId);
    }

    [Fact]
    public async Task SystemAdmin_OrdinaryAndOwnerLevelRoles_StillWork()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        var viewer = AddRole(context, "Viewer");
        var owner = AddRole(context, "BuildingOwner");

        var first = await Import(context, SystemAdmin, "Viewer", dryRun: false, tenantId);
        var second = await new BulkOperationService(context).ImportUsersAsync(
            tenantId, CallerId, Csv("BuildingOwner", "second@example.com"), "users.csv", BulkOperationFormat.CSV, SystemAdmin);

        Assert.Equal(1, first.SuccessRows);
        Assert.Equal(1, second.SuccessRows);
        Assert.Equal(2, context.Users.Count());
        Assert.Equal(new[] { viewer.Id, owner.Id }.OrderBy(i => i), context.UserRoles.Select(ur => ur.RoleId).ToList().OrderBy(i => i));
    }

    [Fact]
    public async Task NoRole_AndUnknownRole_BehaveAsBefore()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();

        var noRole = await Import(context, SystemAdmin, "", dryRun: false, tenantId);
        var unknown = await new BulkOperationService(context).ImportUsersAsync(
            tenantId, CallerId, Csv("NoSuchRole", "other@example.com"), "users.csv", BulkOperationFormat.CSV, SystemAdmin);

        Assert.Equal(1, noRole.SuccessRows);
        Assert.Equal(1, unknown.SuccessRows); // user still created, as today
        Assert.Contains("not found, user created without role", unknown.ErrorDetails);
        Assert.Equal(2, context.Users.Count());
        Assert.Empty(context.UserRoles);
    }

    [Fact]
    public async Task TenantScopedCustomRoleNamedLikeAPlatformRole_IsRefusedToo()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        AddRole(context, "SystemAdmin", tenantId);

        var operation = await Import(context, SystemAdmin, "systemadmin", dryRun: false, tenantId);

        Assert.Empty(context.Users);
        Assert.Empty(context.UserRoles);
        Assert.Equal(1, operation.ErrorRows);
        Assert.Contains("can only be granted by a SuperAdmin", operation.ErrorDetails);
    }

    // ---- the caller level comes from the token, through the real controller ----

    private static async Task<(IActionResult Result, IAMDbContext Context)> ViaController(string callerRole, bool dryRun)
    {
        var context = CreateContext();
        AddRole(context, "SuperAdmin");
        var controller = new BulkOperationsController(new BulkOperationService(context), NullLogger<BulkOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, CallerId.ToString()),
                        new Claim(ClaimTypes.Role, callerRole),
                    }, "test"))
                }
            }
        };
        var bytes = Encoding.UTF8.GetBytes("email,firstname,lastname,role\nnew.user@example.com,New,User,SuperAdmin\n");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "users.csv");
        var result = dryRun
            ? await controller.ImportUsersDryRun(file, Guid.NewGuid(), "csv", default)
            : await controller.ImportUsers(file, Guid.NewGuid(), "csv", default);
        return (result, context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controller_SystemAdminToken_IsRefused_SuperAdminToken_Succeeds(bool dryRun)
    {
        var (denied, deniedContext) = await ViaController("SystemAdmin", dryRun);
        var deniedBody = Assert.IsType<OkObjectResult>(denied).Value!;
        Assert.Equal(1, (int)deniedBody.GetType().GetProperty("errorRows")!.GetValue(deniedBody)!);
        Assert.Empty(deniedContext.Users);

        var (allowed, allowedContext) = await ViaController("SuperAdmin", dryRun);
        var allowedBody = Assert.IsType<OkObjectResult>(allowed).Value!;
        Assert.Equal(1, (int)allowedBody.GetType().GetProperty("successRows")!.GetValue(allowedBody)!);
        Assert.Equal(dryRun ? 0 : 1, allowedContext.Users.Count());
    }
}
