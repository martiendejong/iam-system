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
/// Task 4712: delegations and SoD constraints follow the caller's real authority.
/// - a delegation is created by its delegator (or a global admin for them), only for permissions the
///   delegator holds in that tenant, and wildcard grants always need approval;
/// - the delegator and the delegate can never approve it; an admin of the tenant can;
/// - revoke is for the delegator, the delegate and admins;
/// - SoD constraint create/update/delete is SuperAdmin/SystemAdmin only (list/check stay readable);
/// - delegation reads and effective permissions are scoped to the caller.
/// </summary>
public class DelegationAuthorityTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid TenantAdminRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01");

    private readonly IAMTestWebApplicationFactory _factory;

    public DelegationAuthorityTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ----- helpers ---------------------------------------------------------------------------

    private async Task<Tenant> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<User> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@delegation-authz.test",
            FirstName = "Del",
            LastName = "Egation",
            PasswordHash = "not-used"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A user holding a role with exactly these permissions (role scoped to the tenant, null = global row).</summary>
    private async Task<User> CreateHolderAsync(Guid? tenantId, string[] permissions, DateTime? expiresAt = null)
    {
        var user = await CreateUserAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role-{Guid.NewGuid():N}",
            TenantId = tenantId,
            Permissions = JsonSerializer.Serialize(permissions)
        };
        db.Roles.Add(role);
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = role.Id,
            TenantId = tenantId,
            ExpiresAt = expiresAt,
            GrantedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A user holding the TenantAdmin role scoped to <paramref name="tenantId"/>.</summary>
    private async Task<User> CreateTenantAdminAsync(Guid tenantId)
    {
        var user = await CreateUserAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = TenantAdminRoleId,
            TenantId = tenantId,
            GrantedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Delegation> SeedDelegationAsync(
        Guid delegatorId, Guid delegateId, Guid tenantId, string[] permissions,
        DelegationStatus status = DelegationStatus.Active)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var delegation = new Delegation
        {
            Id = Guid.NewGuid(),
            DelegatorUserId = delegatorId,
            DelegateUserId = delegateId,
            TenantId = tenantId,
            Permissions = JsonSerializer.Serialize(permissions),
            ValidFrom = DateTime.UtcNow.AddMinutes(-5),
            ValidUntil = DateTime.UtcNow.AddDays(7),
            Reason = "seeded",
            Status = status,
            RequiresApproval = status == DelegationStatus.PendingApproval,
            IsActive = status == DelegationStatus.Active
        };
        db.Delegations.Add(delegation);
        await db.SaveChangesAsync();
        return delegation;
    }

    private async Task<Delegation?> GetDelegationRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Delegations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
    }

    private async Task<int> CountDelegationsAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.Delegations.CountAsync(d => d.TenantId == tenantId);
    }

    /// <summary>Client authenticated as <paramref name="userId"/>; default token shape = password login (no tenant_id).</summary>
    private HttpClient ClientAs(Guid userId, string[]? roles = null, string? tenantClaim = null)
    {
        var client = _factory.CreateClient();
        var token = TestAuthenticationHelper.GenerateJwtToken(
            userId, $"{userId:N}@delegation-authz.test", roles ?? new[] { "User" }, tenantClaim);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient SuperAdminClient() => ClientAs(Guid.NewGuid(), new[] { "SuperAdmin" });

    private HttpClient SystemAdminClient() => ClientAs(Guid.NewGuid(), new[] { "SystemAdmin" });

    /// <summary>Request body; delegatorUserId / requiresApproval are left out entirely when not given (as the admin UI does).</summary>
    private static Dictionary<string, object?> DelegationBody(
        Guid tenantId, Guid delegateId, string[]? permissions, Guid? delegatorId = null, bool? requiresApproval = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["delegateUserId"] = delegateId,
            ["tenantId"] = tenantId,
            ["permissions"] = permissions,
            ["validFrom"] = DateTime.UtcNow.AddMinutes(-1),
            ["validUntil"] = DateTime.UtcNow.AddDays(7),
            ["reason"] = "cover during leave"
        };
        if (delegatorId.HasValue)
            body["delegatorUserId"] = delegatorId.Value;
        if (requiresApproval.HasValue)
            body["requiresApproval"] = requiresApproval.Value;
        return body;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<List<string>> EffectivePermissionsAsync(Guid userId, Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAM.Core.Services.IDelegationService>();
        return await service.GetEffectivePermissionsAsync(userId, tenantId);
    }

    private static string StatusOf(JsonElement body) => body.GetProperty("status").GetString()!;

    // ----- create: whose authority --------------------------------------------------------------

    [Fact]
    public async Task Create_AsSelf_DelegatorOmitted_Succeeds_AndDelegateGainsThePermission()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View", "Room.Manage" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("Active", StatusOf(body));
        Assert.Equal(delegator.Id, body.GetProperty("delegatorUserId").GetGuid());
        Assert.Contains("Room.View", await EffectivePermissionsAsync(delegatee.Id, tenant.Id));
        Assert.DoesNotContain("Room.Manage", await EffectivePermissionsAsync(delegatee.Id, tenant.Id));
    }

    [Fact]
    public async Task Create_AsSelf_ExplicitOwnDelegatorId_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }, delegator.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_InSomeoneElsesName_ByPlainUser_IsForbidden_EvenWhenThatPersonHoldsThePermission()
    {
        var tenant = await CreateTenantAsync();
        var victim = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var attacker = await CreateUserAsync();

        var response = await ClientAs(attacker.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, attacker.Id, new[] { "Room.View" }, victim.Id, requiresApproval: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountDelegationsAsync(tenant.Id));
    }

    [Fact]
    public async Task Create_InSomeoneElsesName_ByTenantAdmin_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var victim = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var tenantAdmin = await CreateTenantAdminAsync(tenant.Id);
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(tenantAdmin.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }, victim.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountDelegationsAsync(tenant.Id));
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task Create_InSomeoneElsesName_ByGlobalAdmin_Succeeds_AndAuditRecordsWhoCreatedIt(string adminRole)
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();
        var adminId = Guid.NewGuid();

        var response = await ClientAs(adminId, new[] { adminRole }).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }, delegator.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(delegator.Id, body.GetProperty("delegatorUserId").GetGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == "DelegationCreated" && a.TenantId == tenant.Id);
        Assert.Equal(delegator.Id, audit.UserId);
        using var details = JsonDocument.Parse(audit.Details!);
        Assert.Equal(adminId, details.RootElement.GetProperty("createdByUserId").GetGuid());
    }

    [Fact]
    public async Task Create_AsSelf_AuditRowHasNoCreatedByField()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == "DelegationCreated" && a.TenantId == tenant.Id);
        Assert.Equal(delegator.Id, audit.UserId);
        using var details = JsonDocument.Parse(audit.Details!);
        Assert.False(details.RootElement.TryGetProperty("createdByUserId", out _));
    }

    [Fact]
    public async Task Create_ByGlobalAdmin_StillNeedsTheDelegatorToHoldThePermission()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await SuperAdminClient().PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Billing.Approve" }, delegator.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountDelegationsAsync(tenant.Id));
    }

    // ----- create: which permissions ------------------------------------------------------------

    [Fact]
    public async Task Create_PermissionTheDelegatorDoesNotHold_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Billing.Approve" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountDelegationsAsync(tenant.Id));
        Assert.DoesNotContain("Billing.Approve", await EffectivePermissionsAsync(delegatee.Id, tenant.Id));
    }

    [Fact]
    public async Task Create_OneHeldOneNotHeld_RejectsTheWholeDelegation()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View", "Billing.Approve" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountDelegationsAsync(tenant.Id));
    }

    [Fact]
    public async Task Create_PermissionHeldOnlyInAnotherTenant_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(otherTenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountDelegationsAsync(tenant.Id));
    }

    [Fact]
    public async Task Create_PermissionFromAGlobalRoleAssignment_CanBeDelegated()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(null, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_PermissionFromAnExpiredRole_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" }, DateTime.UtcNow.AddHours(-1));
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_PermissionRedelegatedFromAnActiveDelegation_IsAllowed()
    {
        var tenant = await CreateTenantAsync();
        var owner = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var middle = await CreateUserAsync();
        var last = await CreateUserAsync();
        await SeedDelegationAsync(owner.Id, middle.Id, tenant.Id, new[] { "Room.View" });

        var response = await ClientAs(middle.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, last.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_PermissionFromAPendingDelegation_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var owner = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var middle = await CreateUserAsync();
        var last = await CreateUserAsync();
        await SeedDelegationAsync(owner.Id, middle.Id, tenant.Id, new[] { "Room.View" }, DelegationStatus.PendingApproval);

        var response = await ClientAs(middle.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, last.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_HolderOfAllPermissionsWildcard_CanDelegateASpecificPermission()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "*" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Active", StatusOf(await ReadAsync(response)));
    }

    [Theory]
    [InlineData("Room.View", true)]
    [InlineData("Room.Manage", true)]
    [InlineData("Billing.Approve", false)]
    [InlineData("Room*", false)]
    [InlineData("*", false)]
    public async Task Create_PrefixWildcardHolder_OnlyCoversItsOwnPrefix(string requested, bool allowed)
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.*" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { requested }));

        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- create: approval cannot be skipped -----------------------------------------------------

    [Fact]
    public async Task Create_WildcardGrant_IsPending_EvenWhenTheCallerSaysNoApprovalNeeded()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "*" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "*" }, requiresApproval: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("PendingApproval", StatusOf(body));
        Assert.True(body.GetProperty("requiresApproval").GetBoolean());
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Empty(await EffectivePermissionsAsync(delegatee.Id, tenant.Id));
    }

    [Fact]
    public async Task Create_RequiresApprovalTrue_IsPending_AndGrantsNothingUntilApproved()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }, requiresApproval: true));

        Assert.Equal("PendingApproval", StatusOf(await ReadAsync(response)));
        Assert.DoesNotContain("Room.View", await EffectivePermissionsAsync(delegatee.Id, tenant.Id));
    }

    [Fact]
    public async Task Create_OrdinaryPermission_WithFlagOff_IsActiveImmediately()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }, requiresApproval: false));

        Assert.Equal("Active", StatusOf(await ReadAsync(response)));
    }

    // ----- create: token and input rules ----------------------------------------------------------

    [Fact]
    public async Task Create_WithATokenScopedToAnotherTenant_IsForbidden()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id, tenantClaim: otherTenant.Id.ToString()).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithATokenScopedToTheSameTenant_Succeeds()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id, tenantClaim: tenant.Id.ToString()).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServiceAccountToken_IsForbidden_OnCreateAndList()
    {
        var tenant = await CreateTenantAsync();
        var delegatee = await CreateUserAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestAuthenticationHelper.GenerateServiceAccountToken("svc", "users:create"));

        var response = await client.PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { "users:create" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The create above is also refused by the held-permission check; reads show the token type itself is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/delegations?tenantId={tenant.Id}")).StatusCode);
    }

    [Fact]
    public async Task Create_Unauthenticated_IsUnauthorized()
    {
        var tenant = await CreateTenantAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, Guid.NewGuid(), new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static IEnumerable<object?[]> NoRealPermissions()
    {
        yield return new object?[] { null };
        yield return new object?[] { new string[0] };
        yield return new object?[] { new[] { "", "   " } };
    }

    [Theory]
    [MemberData(nameof(NoRealPermissions))]
    public async Task Create_WithoutAnyRealPermission_IsBadRequest(string[]? permissions)
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, permissions));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ToYourself_IsBadRequest()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegator.Id, new[] { "Room.View" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_TrimsAndDeduplicatesThePermissions()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();

        var response = await ClientAs(delegator.Id).PostAsJsonAsync(
            "/api/delegations", DelegationBody(tenant.Id, delegatee.Id, new[] { " Room.View ", "Room.View" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("[\"Room.View\"]", body.GetProperty("permissions").GetString());
    }

    // ----- approve --------------------------------------------------------------------------------

    private async Task<(Tenant tenant, User delegator, User delegatee, Delegation delegation)> PendingAsync()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();
        var delegation = await SeedDelegationAsync(
            delegator.Id, delegatee.Id, tenant.Id, new[] { "Room.View" }, DelegationStatus.PendingApproval);
        return (tenant, delegator, delegatee, delegation);
    }

    [Fact]
    public async Task Approve_ByTheDelegate_IsForbidden_AndTheDelegationStaysPending()
    {
        var (_, _, delegatee, delegation) = await PendingAsync();

        var response = await ClientAs(delegatee.Id).PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DelegationStatus.PendingApproval, (await GetDelegationRowAsync(delegation.Id))!.Status);
    }

    [Fact]
    public async Task Approve_ByTheDelegator_IsForbidden()
    {
        var (_, delegator, _, delegation) = await PendingAsync();

        var response = await ClientAs(delegator.Id).PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DelegationStatus.PendingApproval, (await GetDelegationRowAsync(delegation.Id))!.Status);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task Approve_ByTheDelegate_IsForbidden_EvenWithAdminRoleClaims(string role)
    {
        var (_, _, delegatee, delegation) = await PendingAsync();

        var response = await ClientAs(delegatee.Id, new[] { role }).PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DelegationStatus.PendingApproval, (await GetDelegationRowAsync(delegation.Id))!.Status);
    }

    [Fact]
    public async Task Approve_ByAGlobalAdminWhoIsTheDelegator_IsForbidden()
    {
        var (_, delegator, _, delegation) = await PendingAsync();

        var response = await ClientAs(delegator.Id, new[] { "SuperAdmin" }).PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Approve_ByAnUnrelatedPlainUser_IsForbidden()
    {
        var (_, _, _, delegation) = await PendingAsync();
        var stranger = await CreateUserAsync();

        var response = await ClientAs(stranger.Id).PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("SystemAdmin")]
    public async Task Approve_ByADifferentGlobalAdmin_Succeeds_AndTheDelegateGainsThePermission(string role)
    {
        var (tenant, _, delegatee, delegation) = await PendingAsync();
        var adminId = Guid.NewGuid();

        var response = await ClientAs(adminId, new[] { role }).PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = (await GetDelegationRowAsync(delegation.Id))!;
        Assert.Equal(DelegationStatus.Active, row.Status);
        Assert.True(row.IsActive);
        Assert.Equal(adminId, row.ApprovedByUserId);
        Assert.Contains("Room.View", await EffectivePermissionsAsync(delegatee.Id, tenant.Id));
    }

    [Fact]
    public async Task Approve_ByAnAdminOfThatTenant_Succeeds()
    {
        var (tenant, _, _, delegation) = await PendingAsync();
        var tenantAdmin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(tenantAdmin.Id).PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Approve_ByAnAdminOfAnotherTenant_IsForbidden()
    {
        var (_, _, _, delegation) = await PendingAsync();
        var otherTenant = await CreateTenantAsync();
        var foreignAdmin = await CreateTenantAdminAsync(otherTenant.Id);

        var response = await ClientAs(foreignAdmin.Id).PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DelegationStatus.PendingApproval, (await GetDelegationRowAsync(delegation.Id))!.Status);
    }

    [Fact]
    public async Task Approve_ByATenantAdminWithATokenScopedToAnotherTenant_IsForbidden()
    {
        var (tenant, _, _, delegation) = await PendingAsync();
        var otherTenant = await CreateTenantAsync();
        var tenantAdmin = await CreateTenantAdminAsync(tenant.Id);

        var response = await ClientAs(tenantAdmin.Id, tenantClaim: otherTenant.Id.ToString())
            .PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Approve_WhenTheDelegatorNoLongerHoldsThePermission_IsRejected_AndStaysPending()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateUserAsync(); // holds nothing: a forged/stale pending row
        var delegatee = await CreateUserAsync();
        var delegation = await SeedDelegationAsync(
            delegator.Id, delegatee.Id, tenant.Id, new[] { "Billing.Approve" }, DelegationStatus.PendingApproval);

        var response = await SuperAdminClient().PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(DelegationStatus.PendingApproval, (await GetDelegationRowAsync(delegation.Id))!.Status);
        Assert.Empty(await EffectivePermissionsAsync(delegatee.Id, tenant.Id));
    }

    [Fact]
    public async Task Approve_ADelegationThatIsNotPending_IsBadRequest()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();
        var delegation = await SeedDelegationAsync(delegator.Id, delegatee.Id, tenant.Id, new[] { "Room.View" });

        var response = await SuperAdminClient().PostAsync($"/api/delegations/{delegation.Id}/approve", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Approve_UnknownDelegation_IsBadRequest()
    {
        var response = await SuperAdminClient().PostAsync($"/api/delegations/{Guid.NewGuid()}/approve", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ----- revoke ---------------------------------------------------------------------------------

    private async Task<(Tenant tenant, User delegator, User delegatee, Delegation delegation)> ActiveAsync()
    {
        var tenant = await CreateTenantAsync();
        var delegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var delegatee = await CreateUserAsync();
        var delegation = await SeedDelegationAsync(delegator.Id, delegatee.Id, tenant.Id, new[] { "Room.View" });
        return (tenant, delegator, delegatee, delegation);
    }

    [Theory]
    [InlineData("delegator")]
    [InlineData("delegate")]
    [InlineData("superadmin")]
    [InlineData("systemadmin")]
    [InlineData("tenantadmin")]
    public async Task Revoke_ByDelegatorDelegateOrAdmin_Succeeds_AndTheDelegateLosesThePermission(string who)
    {
        var (tenant, delegator, delegatee, delegation) = await ActiveAsync();
        var client = who switch
        {
            "delegator" => ClientAs(delegator.Id),
            "delegate" => ClientAs(delegatee.Id),
            "superadmin" => SuperAdminClient(),
            "systemadmin" => SystemAdminClient(),
            _ => ClientAs((await CreateTenantAdminAsync(tenant.Id)).Id)
        };

        var response = await client.PostAsync($"/api/delegations/{delegation.Id}/revoke", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DelegationStatus.Revoked, (await GetDelegationRowAsync(delegation.Id))!.Status);
        Assert.DoesNotContain("Room.View", await EffectivePermissionsAsync(delegatee.Id, tenant.Id));
    }

    [Fact]
    public async Task Revoke_ByAnUnrelatedPlainUser_IsForbidden_AndTheDelegationStaysActive()
    {
        var (_, _, _, delegation) = await ActiveAsync();
        var stranger = await CreateUserAsync();

        var response = await ClientAs(stranger.Id).PostAsync($"/api/delegations/{delegation.Id}/revoke", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DelegationStatus.Active, (await GetDelegationRowAsync(delegation.Id))!.Status);
    }

    [Fact]
    public async Task Revoke_ByAnAdminOfAnotherTenant_IsForbidden()
    {
        var (_, _, _, delegation) = await ActiveAsync();
        var otherTenant = await CreateTenantAsync();
        var foreignAdmin = await CreateTenantAdminAsync(otherTenant.Id);

        var response = await ClientAs(foreignAdmin.Id).PostAsync($"/api/delegations/{delegation.Id}/revoke", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DelegationStatus.Active, (await GetDelegationRowAsync(delegation.Id))!.Status);
    }

    [Fact]
    public async Task Revoke_UnknownDelegation_IsBadRequest()
    {
        var response = await SuperAdminClient().PostAsync($"/api/delegations/{Guid.NewGuid()}/revoke", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ----- reads ----------------------------------------------------------------------------------

    [Fact]
    public async Task List_ShowsAParticipantOnlyTheirOwnDelegations()
    {
        var (tenant, delegator, delegatee, delegation) = await ActiveAsync();
        var otherDelegator = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var unrelated = await SeedDelegationAsync(otherDelegator.Id, (await CreateUserAsync()).Id, tenant.Id, new[] { "Room.View" });

        foreach (var participant in new[] { delegator, delegatee })
        {
            var response = await ClientAs(participant.Id).GetAsync($"/api/delegations?tenantId={tenant.Id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var ids = (await ReadAsync(response)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
            Assert.Equal(new[] { delegation.Id }, ids);
            Assert.DoesNotContain(unrelated.Id, ids);
        }
    }

    [Fact]
    public async Task List_ShowsAStrangerNothing()
    {
        var (tenant, _, _, _) = await ActiveAsync();
        var stranger = await CreateUserAsync();

        var response = await ClientAs(stranger.Id).GetAsync($"/api/delegations?tenantId={tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ReadAsync(response)).EnumerateArray());
    }

    [Fact]
    public async Task List_ShowsAnAdminOfTheTenantAndAGlobalAdminEverything()
    {
        var (tenant, _, _, delegation) = await ActiveAsync();
        var tenantAdmin = await CreateTenantAdminAsync(tenant.Id);

        foreach (var client in new[] { ClientAs(tenantAdmin.Id), SuperAdminClient(), SystemAdminClient() })
        {
            var response = await client.GetAsync($"/api/delegations?tenantId={tenant.Id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var ids = (await ReadAsync(response)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
            Assert.Contains(delegation.Id, ids);
        }
    }

    [Fact]
    public async Task List_AnAdminOfAnotherTenantSeesNothingOfThisTenant()
    {
        var (tenant, _, _, _) = await ActiveAsync();
        var otherTenant = await CreateTenantAsync();
        var foreignAdmin = await CreateTenantAdminAsync(otherTenant.Id);

        var response = await ClientAs(foreignAdmin.Id).GetAsync($"/api/delegations?tenantId={tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ReadAsync(response)).EnumerateArray());
    }

    [Theory]
    [InlineData("delegator", HttpStatusCode.OK)]
    [InlineData("delegate", HttpStatusCode.OK)]
    [InlineData("superadmin", HttpStatusCode.OK)]
    [InlineData("tenantadmin", HttpStatusCode.OK)]
    [InlineData("stranger", HttpStatusCode.Forbidden)]
    public async Task GetOne_IsForParticipantsAndAdminsOnly(string who, HttpStatusCode expected)
    {
        var (tenant, delegator, delegatee, delegation) = await ActiveAsync();
        var client = who switch
        {
            "delegator" => ClientAs(delegator.Id),
            "delegate" => ClientAs(delegatee.Id),
            "superadmin" => SuperAdminClient(),
            "tenantadmin" => ClientAs((await CreateTenantAdminAsync(tenant.Id)).Id),
            _ => ClientAs((await CreateUserAsync()).Id)
        };

        var response = await client.GetAsync($"/api/delegations/{delegation.Id}");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task GetOne_Unknown_IsNotFound()
    {
        var response = await SuperAdminClient().GetAsync($"/api/delegations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EffectivePermissions_OfYourself_AreReadable()
    {
        var tenant = await CreateTenantAsync();
        var user = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });

        var response = await ClientAs(user.Id).GetAsync($"/api/delegations/effective-permissions?userId={user.Id}&tenantId={tenant.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var permissions = (await ReadAsync(response)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("Room.View", permissions);
    }

    [Fact]
    public async Task EffectivePermissions_OfSomeoneElse_AreForbiddenToAPlainUser()
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var stranger = await CreateUserAsync();

        var response = await ClientAs(stranger.Id).GetAsync($"/api/delegations/effective-permissions?userId={target.Id}&tenantId={tenant.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EffectivePermissions_OfSomeoneElse_AreReadableByAnAdminOfTheTenantAndGlobalAdmins()
    {
        var tenant = await CreateTenantAsync();
        var target = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var tenantAdmin = await CreateTenantAdminAsync(tenant.Id);

        foreach (var client in new[] { ClientAs(tenantAdmin.Id), SuperAdminClient(), SystemAdminClient() })
        {
            var response = await client.GetAsync($"/api/delegations/effective-permissions?userId={target.Id}&tenantId={tenant.Id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task EffectivePermissions_OfSomeoneElse_AreForbiddenToAnAdminOfAnotherTenant()
    {
        var tenant = await CreateTenantAsync();
        var otherTenant = await CreateTenantAsync();
        var target = await CreateHolderAsync(tenant.Id, new[] { "Room.View" });
        var foreignAdmin = await CreateTenantAdminAsync(otherTenant.Id);

        var response = await ClientAs(foreignAdmin.Id).GetAsync($"/api/delegations/effective-permissions?userId={target.Id}&tenantId={tenant.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- SoD constraints ------------------------------------------------------------------------

    private async Task<(Tenant tenant, Role roleA, Role roleB)> TenantWithTwoRolesAsync()
    {
        var tenant = await CreateTenantAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var roleA = new Role { Id = Guid.NewGuid(), Name = $"Requester-{Guid.NewGuid():N}", TenantId = tenant.Id };
        var roleB = new Role { Id = Guid.NewGuid(), Name = $"Approver-{Guid.NewGuid():N}", TenantId = tenant.Id };
        db.Roles.AddRange(roleA, roleB);
        await db.SaveChangesAsync();
        return (tenant, roleA, roleB);
    }

    private async Task<SodConstraint> SeedConstraintAsync(Tenant tenant, Role roleA, Role roleB)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var constraint = new SodConstraint
        {
            Id = Guid.NewGuid(),
            Name = "Requester vs Approver",
            TenantId = tenant.Id,
            ConflictingRoleA = roleA.Id,
            ConflictingRoleB = roleB.Id,
            Description = "seeded",
            Severity = SodSeverity.Block
        };
        db.SodConstraints.Add(constraint);
        await db.SaveChangesAsync();
        return constraint;
    }

    private async Task<SodConstraint?> GetConstraintRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return await db.SodConstraints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    private static object ConstraintBody(Tenant tenant, Role roleA, Role roleB) => new
    {
        name = "Requester vs Approver",
        tenantId = tenant.Id,
        conflictingRoleA = roleA.Id,
        conflictingRoleB = roleB.Id,
        description = "must not hold both",
        severity = "Block"
    };

    private static object UpdateBody() => new { name = "Renamed", description = "changed", severity = "Warning", isActive = false };

    public enum SodCaller { Anonymous, PlainUser, TenantAdmin, SuperAdmin, SystemAdmin }

    private async Task<HttpClient> SodClientAsync(SodCaller caller, Tenant tenant) => caller switch
    {
        SodCaller.Anonymous => _factory.CreateClient(),
        SodCaller.PlainUser => ClientAs((await CreateUserAsync()).Id),
        SodCaller.TenantAdmin => ClientAs((await CreateTenantAdminAsync(tenant.Id)).Id),
        SodCaller.SuperAdmin => SuperAdminClient(),
        _ => SystemAdminClient()
    };

    [Theory]
    [InlineData(SodCaller.PlainUser, HttpStatusCode.Forbidden)]
    [InlineData(SodCaller.TenantAdmin, HttpStatusCode.Forbidden)]
    [InlineData(SodCaller.Anonymous, HttpStatusCode.Unauthorized)]
    [InlineData(SodCaller.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(SodCaller.SystemAdmin, HttpStatusCode.OK)]
    public async Task SodConstraint_Create_IsAdminOnly(SodCaller caller, HttpStatusCode expected)
    {
        var (tenant, roleA, roleB) = await TenantWithTwoRolesAsync();
        var client = await SodClientAsync(caller, tenant);

        var response = await client.PostAsJsonAsync("/api/sod/constraints", ConstraintBody(tenant, roleA, roleB));

        Assert.Equal(expected, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var stored = await db.SodConstraints.CountAsync(c => c.TenantId == tenant.Id);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, stored);
    }

    [Theory]
    [InlineData(SodCaller.PlainUser, HttpStatusCode.Forbidden)]
    [InlineData(SodCaller.TenantAdmin, HttpStatusCode.Forbidden)]
    [InlineData(SodCaller.Anonymous, HttpStatusCode.Unauthorized)]
    [InlineData(SodCaller.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(SodCaller.SystemAdmin, HttpStatusCode.OK)]
    public async Task SodConstraint_Update_IsAdminOnly(SodCaller caller, HttpStatusCode expected)
    {
        var (tenant, roleA, roleB) = await TenantWithTwoRolesAsync();
        var constraint = await SeedConstraintAsync(tenant, roleA, roleB);
        var client = await SodClientAsync(caller, tenant);

        var response = await client.PutAsJsonAsync($"/api/sod/constraints/{constraint.Id}", UpdateBody());

        Assert.Equal(expected, response.StatusCode);
        var row = (await GetConstraintRowAsync(constraint.Id))!;
        Assert.Equal(expected == HttpStatusCode.OK ? "Renamed" : "Requester vs Approver", row.Name);
        Assert.Equal(expected != HttpStatusCode.OK, row.IsActive);
    }

    [Theory]
    [InlineData(SodCaller.PlainUser, HttpStatusCode.Forbidden)]
    [InlineData(SodCaller.TenantAdmin, HttpStatusCode.Forbidden)]
    [InlineData(SodCaller.Anonymous, HttpStatusCode.Unauthorized)]
    [InlineData(SodCaller.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(SodCaller.SystemAdmin, HttpStatusCode.OK)]
    public async Task SodConstraint_Delete_IsAdminOnly(SodCaller caller, HttpStatusCode expected)
    {
        var (tenant, roleA, roleB) = await TenantWithTwoRolesAsync();
        var constraint = await SeedConstraintAsync(tenant, roleA, roleB);
        var client = await SodClientAsync(caller, tenant);

        var response = await client.DeleteAsync($"/api/sod/constraints/{constraint.Id}");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected != HttpStatusCode.OK, await GetConstraintRowAsync(constraint.Id) != null);
    }

    [Fact]
    public async Task SodConstraint_ListGetCheckAndViolations_StayReadableForAPlainUser()
    {
        var (tenant, roleA, roleB) = await TenantWithTwoRolesAsync();
        var constraint = await SeedConstraintAsync(tenant, roleA, roleB);
        var user = await CreateUserAsync();
        var client = ClientAs(user.Id);

        var list = await client.GetAsync($"/api/sod/constraints?tenantId={tenant.Id}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single((await ReadAsync(list)).EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/sod/constraints/{constraint.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/sod/check/{user.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/sod/violations?tenantId={tenant.Id}")).StatusCode);
    }
}
