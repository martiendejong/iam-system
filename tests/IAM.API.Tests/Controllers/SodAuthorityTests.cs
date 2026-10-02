using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 4741 (follows 4712, which locked SoD rule create/update/delete): the rest of the SoD surface follows the caller's
/// authority too.
/// - resolving a violation: global admin or admin of the violation's tenant, never the user it is about (even an admin);
/// - violations list: all for a global admin (no tenant) or an admin of the asked tenant, otherwise only your own;
/// - check for another user: global admin or an admin of every tenant the user holds roles in; your own id always works;
/// - rule reads (list, get): admin of the tenant only.
/// A refusal changes nothing (no violation change, no violation or audit rows).
/// </summary>
public class SodAuthorityTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private readonly IAMTestWebApplicationFactory _factory;

    public SodAuthorityTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ----- helpers ---------------------------------------------------------------------------

    private IAMDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAMDbContext>();

    private async Task<Tenant> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = Db(scope);
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<Role> CreateRoleAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = Db(scope);
        var role = new Role { Id = Guid.NewGuid(), Name = $"Role-{Guid.NewGuid():N}", TenantId = tenantId, Permissions = "[]" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private async Task<User> CreateUserAsync(params (Guid RoleId, Guid? TenantId)[] roles)
    {
        using var scope = _factory.Services.CreateScope();
        var db = Db(scope);
        var user = new User
        {
            Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@sod-authz.test", FirstName = "Sod", LastName = "Authz", PasswordHash = "not-used"
        };
        db.Users.Add(user);
        foreach (var (roleId, tenantId) in roles)
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = roleId, TenantId = tenantId, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return user;
    }

    private Task<User> CreateTenantAdminAsync(Guid tenantId) => CreateUserAsync((TenantAdminRoleId, tenantId));

    private HttpClient ClientAs(Guid userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(
            userId, $"{userId:N}@sod-authz.test", roles.Length == 0 ? new[] { "User" } : roles, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed record World(Tenant A, Tenant B, Role RoleA, Role RoleB, SodConstraint Constraint, User Affected);

    /// <summary>Tenant A with a rule (RoleA vs RoleB), a second tenant B, and a user the (open) violation is about.</summary>
    private async Task<World> CreateWorldAsync()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();
        var roleA = await CreateRoleAsync(a.Id);
        var roleB = await CreateRoleAsync(a.Id);
        using var scope = _factory.Services.CreateScope();
        var db = Db(scope);
        var constraint = new SodConstraint
        {
            Id = Guid.NewGuid(), Name = "Requester vs Approver", TenantId = a.Id,
            ConflictingRoleA = roleA.Id, ConflictingRoleB = roleB.Id, Description = "seeded", Severity = SodSeverity.Block
        };
        db.SodConstraints.Add(constraint);
        await db.SaveChangesAsync();
        var affected = await CreateUserAsync((roleA.Id, a.Id), (roleB.Id, a.Id));
        return new World(a, b, roleA, roleB, constraint, affected);
    }

    private async Task<SodViolation> SeedViolationAsync(World w, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = Db(scope);
        var violation = new SodViolation
        {
            Id = Guid.NewGuid(), ConstraintId = w.Constraint.Id, UserId = userId,
            RoleA = w.RoleA.Id, RoleB = w.RoleB.Id, DetectedAt = DateTime.UtcNow
        };
        db.SodViolations.Add(violation);
        await db.SaveChangesAsync();
        return violation;
    }

    private async Task<SodViolation> ViolationRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).SodViolations.AsNoTracking().FirstAsync(v => v.Id == id);
    }

    private async Task<int> AuditRowsAsync(string action, Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).AuditLogs.CountAsync(a => a.Action == action && a.TenantId == tenantId);
    }

    private async Task<int> ViolationCountAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await Db(scope).SodViolations.CountAsync(v => v.UserId == userId);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static HashSet<Guid> Ids(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToHashSet();

    private static StringContent Resolution() => new("{\"resolution\":\"accepted risk\"}", System.Text.Encoding.UTF8, "application/json");

    public enum Caller { Anonymous, PlainUser, TenantAdminOfOtherTenant, TenantAdmin, SuperAdmin, SystemAdmin }

    private async Task<HttpClient> ClientForAsync(Caller caller, World w) => caller switch
    {
        Caller.Anonymous => _factory.CreateClient(),
        Caller.PlainUser => ClientAs((await CreateUserAsync()).Id),
        Caller.TenantAdminOfOtherTenant => ClientAs((await CreateTenantAdminAsync(w.B.Id)).Id),
        Caller.TenantAdmin => ClientAs((await CreateTenantAdminAsync(w.A.Id)).Id),
        Caller.SuperAdmin => ClientAs(Guid.NewGuid(), "SuperAdmin"),
        Caller.SystemAdmin => ClientAs(Guid.NewGuid(), "SystemAdmin"),
        _ => throw new ArgumentOutOfRangeException(nameof(caller))
    };

    // ----- resolve --------------------------------------------------------------------------------

    [Theory]
    [InlineData(Caller.Anonymous, HttpStatusCode.Unauthorized)]
    [InlineData(Caller.PlainUser, HttpStatusCode.Forbidden)]
    [InlineData(Caller.TenantAdminOfOtherTenant, HttpStatusCode.Forbidden)]
    [InlineData(Caller.TenantAdmin, HttpStatusCode.OK)]
    [InlineData(Caller.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(Caller.SystemAdmin, HttpStatusCode.OK)]
    public async Task Resolve_FollowsTheCallersAuthority(Caller caller, HttpStatusCode expected)
    {
        var w = await CreateWorldAsync();
        var violation = await SeedViolationAsync(w, w.Affected.Id);
        var client = await ClientForAsync(caller, w);

        var response = await client.PostAsync($"/api/sod/violations/{violation.Id}/resolve", Resolution());

        Assert.Equal(expected, response.StatusCode);
        var row = await ViolationRowAsync(violation.Id);
        if (expected == HttpStatusCode.OK)
        {
            Assert.NotNull(row.ResolvedAt);
            Assert.Equal("accepted risk", row.Resolution);
        }
        else
        {
            Assert.Null(row.ResolvedAt);
            Assert.Null(row.Resolution);
            Assert.Null(row.ResolvedByUserId);
        }
    }

    [Fact]
    public async Task Resolve_RefusalWritesNoAuditRow_AndAnAdminsResolveWritesOne()
    {
        var w = await CreateWorldAsync();
        var violation = await SeedViolationAsync(w, w.Affected.Id);
        var before = await AuditRowsAsync("SodViolationResolved", w.A.Id);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await (await ClientForAsync(Caller.PlainUser, w)).PostAsync($"/api/sod/violations/{violation.Id}/resolve", Resolution())).StatusCode);
        Assert.Equal(before, await AuditRowsAsync("SodViolationResolved", w.A.Id));

        Assert.Equal(HttpStatusCode.OK,
            (await (await ClientForAsync(Caller.TenantAdmin, w)).PostAsync($"/api/sod/violations/{violation.Id}/resolve", Resolution())).StatusCode);
        Assert.Equal(before + 1, await AuditRowsAsync("SodViolationResolved", w.A.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_TheAffectedUserCannot_EvenAsAnAdmin(bool globalAdmin)
    {
        var w = await CreateWorldAsync();
        // The user the violation is about also administers the tenant (or is a global admin).
        var affected = globalAdmin
            ? w.Affected
            : await CreateUserAsync((TenantAdminRoleId, w.A.Id), (w.RoleA.Id, w.A.Id), (w.RoleB.Id, w.A.Id));
        var violation = await SeedViolationAsync(w, affected.Id);
        var client = globalAdmin ? ClientAs(affected.Id, "SuperAdmin") : ClientAs(affected.Id);

        var response = await client.PostAsync($"/api/sod/violations/{violation.Id}/resolve", Resolution());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await ViolationRowAsync(violation.Id)).ResolvedAt);
    }

    // ----- violations list -----------------------------------------------------------------------

    [Fact]
    public async Task Violations_WithoutTenant_GlobalAdminSeesAll_EveryoneElseOnlyTheirOwn()
    {
        var w = await CreateWorldAsync();
        var others = await SeedViolationAsync(w, w.Affected.Id);
        var tenantAdmin = await CreateUserAsync((TenantAdminRoleId, w.A.Id), (w.RoleA.Id, w.A.Id), (w.RoleB.Id, w.A.Id));
        var own = await SeedViolationAsync(w, tenantAdmin.Id);

        var global = Ids(await ReadAsync(await ClientAs(Guid.NewGuid(), "SuperAdmin").GetAsync("/api/sod/violations")));
        Assert.Contains(others.Id, global);
        Assert.Contains(own.Id, global);

        // A tenant admin without a tenant filter sees only what is about themselves.
        var adminList = await ClientAs(tenantAdmin.Id).GetAsync("/api/sod/violations");
        Assert.Equal(HttpStatusCode.OK, adminList.StatusCode);
        Assert.Equal(new[] { own.Id }, Ids(await ReadAsync(adminList)));

        // A plain user sees only their own.
        var plain = await ClientAs(w.Affected.Id).GetAsync("/api/sod/violations");
        Assert.Equal(new[] { others.Id }, Ids(await ReadAsync(plain)));

        // A user with no violations gets an empty list, not someone else's.
        var nobody = await ClientAs((await CreateUserAsync()).Id).GetAsync("/api/sod/violations");
        Assert.Empty((await ReadAsync(nobody)).EnumerateArray());
    }

    [Fact]
    public async Task Violations_WithTenant_AdminOfThatTenantSeesAll_OthersOnlyTheirOwn()
    {
        var w = await CreateWorldAsync();
        var violation = await SeedViolationAsync(w, w.Affected.Id);
        var url = $"/api/sod/violations?tenantId={w.A.Id}";

        var admin = await ReadAsync(await (await ClientForAsync(Caller.TenantAdmin, w)).GetAsync(url));
        Assert.Contains(violation.Id, Ids(admin));

        var global = await ReadAsync(await (await ClientForAsync(Caller.SystemAdmin, w)).GetAsync(url));
        Assert.Contains(violation.Id, Ids(global));

        // An admin of ANOTHER tenant, and a plain user, do not see it ...
        foreach (var caller in new[] { Caller.TenantAdminOfOtherTenant, Caller.PlainUser })
        {
            var response = await (await ClientForAsync(caller, w)).GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(violation.Id, Ids(await ReadAsync(response)));
        }

        // ... but the user it is about still does.
        var own = await ReadAsync(await ClientAs(w.Affected.Id).GetAsync(url));
        Assert.Equal(new[] { violation.Id }, Ids(own));
    }

    // ----- check ----------------------------------------------------------------------------------

    [Fact]
    public async Task Check_OwnId_StillWorks_AndRecordsTheViolation()
    {
        var w = await CreateWorldAsync();

        var response = await ClientAs(w.Affected.Id).PostAsync($"/api/sod/check/{w.Affected.Id}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("violationsFound").GetInt32() > 0);
        Assert.True(await ViolationCountAsync(w.Affected.Id) > 0);
    }

    [Theory]
    [InlineData(Caller.PlainUser, HttpStatusCode.Forbidden)]
    [InlineData(Caller.TenantAdminOfOtherTenant, HttpStatusCode.Forbidden)]
    [InlineData(Caller.TenantAdmin, HttpStatusCode.OK)]
    [InlineData(Caller.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(Caller.SystemAdmin, HttpStatusCode.OK)]
    public async Task Check_ForAnotherUser_NeedsAuthorityOverTheirTenant(Caller caller, HttpStatusCode expected)
    {
        var w = await CreateWorldAsync();
        var client = await ClientForAsync(caller, w);
        var auditBefore = await AuditRowsAsync("SodViolationDetected", w.A.Id);

        var response = await client.PostAsync($"/api/sod/check/{w.Affected.Id}", null);

        Assert.Equal(expected, response.StatusCode);
        // A refusal writes neither violation rows nor audit rows.
        var allowed = expected == HttpStatusCode.OK;
        // (the existing check records one row per role row, so "recorded" means at least one)
        Assert.Equal(allowed, await ViolationCountAsync(w.Affected.Id) > 0);
        Assert.Equal(allowed, await AuditRowsAsync("SodViolationDetected", w.A.Id) > auditBefore);
    }

    [Fact]
    public async Task Check_AUserWithRolesInTwoTenants_NeedsAnAdminOfBoth()
    {
        var w = await CreateWorldAsync();
        var roleInB = await CreateRoleAsync(w.B.Id);
        var multi = await CreateUserAsync((w.RoleA.Id, w.A.Id), (w.RoleB.Id, w.A.Id), (roleInB.Id, w.B.Id));
        var adminOfA = ClientAs((await CreateTenantAdminAsync(w.A.Id)).Id);
        var adminOfBoth = ClientAs((await CreateUserAsync((TenantAdminRoleId, w.A.Id), (TenantAdminRoleId, w.B.Id))).Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await adminOfA.PostAsync($"/api/sod/check/{multi.Id}", null)).StatusCode);
        Assert.Equal(0, await ViolationCountAsync(multi.Id));

        Assert.Equal(HttpStatusCode.OK, (await adminOfBoth.PostAsync($"/api/sod/check/{multi.Id}", null)).StatusCode);
        Assert.True(await ViolationCountAsync(multi.Id) > 0);
    }

    [Fact]
    public async Task Check_AUserWithoutRoles_OrWithATenantlessRole_NeedsAGlobalAdmin()
    {
        var w = await CreateWorldAsync();
        var noRoles = await CreateUserAsync();
        var tenantless = await CreateUserAsync((w.RoleA.Id, w.A.Id), (w.RoleB.Id, null));
        var tenantAdmin = ClientAs((await CreateTenantAdminAsync(w.A.Id)).Id);
        var global = ClientAs(Guid.NewGuid(), "SuperAdmin");

        foreach (var user in new[] { noRoles, tenantless })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.PostAsync($"/api/sod/check/{user.Id}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await global.PostAsync($"/api/sod/check/{user.Id}", null)).StatusCode);
        }
    }

    // ----- rule reads -----------------------------------------------------------------------------

    [Theory]
    [InlineData(Caller.Anonymous, HttpStatusCode.Unauthorized)]
    [InlineData(Caller.PlainUser, HttpStatusCode.Forbidden)]
    [InlineData(Caller.TenantAdminOfOtherTenant, HttpStatusCode.Forbidden)]
    [InlineData(Caller.TenantAdmin, HttpStatusCode.OK)]
    [InlineData(Caller.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(Caller.SystemAdmin, HttpStatusCode.OK)]
    public async Task ConstraintReads_AreAdminOfTenantOnly(Caller caller, HttpStatusCode expected)
    {
        var w = await CreateWorldAsync();
        var client = await ClientForAsync(caller, w);

        var list = await client.GetAsync($"/api/sod/constraints?tenantId={w.A.Id}");
        var one = await client.GetAsync($"/api/sod/constraints/{w.Constraint.Id}");

        Assert.Equal(expected, list.StatusCode);
        Assert.Equal(expected, one.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Contains(w.Constraint.Id, Ids(await ReadAsync(list)));
            Assert.Equal(w.Constraint.Id, (await ReadAsync(one)).GetProperty("id").GetGuid());
        }
    }

    [Fact]
    public async Task ConstraintGet_UnknownId_IsStillANotFound_ForAnAdmin()
    {
        var response = await ClientAs(Guid.NewGuid(), "SuperAdmin").GetAsync($"/api/sod/constraints/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
