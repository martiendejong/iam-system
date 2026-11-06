using IAM.Core.Entities;
using IAM.Core.Services;

namespace IAM.Core.Tests;

/// <summary>
/// Task 5147: the one grant rule (role change, invitation, bulk invite, invitation acceptance) also refuses federated
/// application roles and other privileged roles for everyone but a SuperAdmin, decided on name and category.
/// </summary>
public class TenantRoleGrantRulesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static Role RoleOf(string name, Guid? tenantId = null, string? category = null, string permissions = "[\"Room.View\"]") =>
        new() { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, Category = category, Permissions = permissions };

    // ----- federated app roles ------------------------------------------------------------------

    [Theory]
    [InlineData("taskmanager:admin")]
    [InlineData("app:vault:approver")]
    [InlineData("app:fedha:admin")]
    [InlineData("app:jengomail:admin")]
    public void FederatedAppRole_ByName_IsRefused_ForManagerAndOwner(string name)
    {
        var role = RoleOf(name, category: "app:" + name.Split(':')[0]);

        Assert.Equal(RoleGrantProblem.FederatedAppRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Manager));
        Assert.Equal(RoleGrantProblem.FederatedAppRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Owner));
    }

    [Fact]
    public void FederatedAppRole_ByCategory_IsRefused_EvenWithAnOrdinaryName()
    {
        var role = RoleOf("reviewer", category: "app:taskmanager");

        Assert.Equal(RoleGrantProblem.FederatedAppRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Manager));
        Assert.Equal(RoleGrantProblem.FederatedAppRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Owner));
    }

    [Fact]
    public void FederatedAppRole_ByClientRoleName_IsRefused_WithoutACategory()
    {
        var role = RoleOf("lango:member");

        Assert.Equal(RoleGrantProblem.FederatedAppRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Manager));
    }

    [Fact]
    public void CustomTenantRole_NamedLikeAnAppRole_IsRefused_EvenThoughTheTenantOwnsIt()
    {
        // A tenant cannot launder an app role through its own custom role: the check is the name, not Role.TenantId.
        var role = RoleOf("taskmanager:admin", tenantId: Tenant, category: null);

        Assert.Equal(RoleGrantProblem.FederatedAppRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Owner));
    }

    [Fact]
    public void CategoryPrefix_IsCaseInsensitive()
    {
        var role = RoleOf("reviewer", category: "APP:Vault");

        Assert.Equal(RoleGrantProblem.FederatedAppRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Manager));
    }

    // ----- other privileged roles ---------------------------------------------------------------

    [Theory]
    [InlineData("workspace-admin")]
    [InlineData("Billing Administrator")]
    [InlineData("TENANTADMINISTRATOR")]
    public void AdminNamedRole_IsRefused_ForManagerAndOwner(string name)
    {
        var role = RoleOf(name);

        Assert.Equal(RoleGrantProblem.PrivilegedRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Manager));
        Assert.Equal(RoleGrantProblem.PrivilegedRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Owner));
    }

    [Theory]
    [InlineData("[\"*\"]")]
    [InlineData("[\"Room.*\"]")]
    [InlineData("not json")]
    public void WildcardOrUnreadablePermissions_AreRefused(string permissions)
    {
        var role = RoleOf("operator", permissions: permissions);

        Assert.Equal(RoleGrantProblem.PrivilegedRole, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Owner));
    }

    [Fact]
    public void PlatformAndOwnerLevelRoles_KeepTheirOwnProblems()
    {
        Assert.Equal(RoleGrantProblem.PlatformRole, TenantRoleGrantRules.Check(RoleOf("SystemAdmin"), Tenant, TenantGrantor.Owner));
        Assert.Equal(RoleGrantProblem.OwnerLevelRole, TenantRoleGrantRules.Check(RoleOf("BuildingOwner"), Tenant, TenantGrantor.Manager));
        Assert.Equal(RoleGrantProblem.OwnerLevelRole, TenantRoleGrantRules.Check(RoleOf("TenantAdmin"), Tenant, TenantGrantor.Manager));
    }

    // ----- SuperAdmin ---------------------------------------------------------------------------

    [Theory]
    [InlineData("taskmanager:admin", "app:taskmanager", "[]")]
    [InlineData("app:vault:approver", "app:vault", "[]")]
    [InlineData("workspace-admin", null, "[]")]
    [InlineData("operator", null, "[\"*\"]")]
    [InlineData("SystemAdmin", null, "[\"*\"]")]
    public void SuperAdmin_CanGrantAll(string name, string? category, string permissions)
    {
        var role = RoleOf(name, category: category, permissions: permissions);

        Assert.Equal(RoleGrantProblem.None, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.SuperAdmin));
    }

    // ----- ordinary roles keep working ----------------------------------------------------------

    [Theory]
    [InlineData("User")]
    [InlineData("Resident")]
    [InlineData("Maintenance")]
    [InlineData("Viewer")]
    public void OrdinaryRoles_StillWork_ForManagerAndOwner(string name)
    {
        var role = RoleOf(name, permissions: "[\"Room.View\",\"Device.View\"]");

        Assert.Equal(RoleGrantProblem.None, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Manager));
        Assert.Equal(RoleGrantProblem.None, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Owner));
    }

    [Fact]
    public void BuildingManager_CanStillBeGranted_ByManagerAndOwner()
    {
        var role = RoleOf("BuildingManager", permissions: "[\"Building.View\",\"Room.Manage\"]");

        Assert.Equal(RoleGrantProblem.None, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Manager));
        Assert.Equal(RoleGrantProblem.None, TenantRoleGrantRules.Check(role, Tenant, TenantGrantor.Owner));
    }

    [Fact]
    public void OwnerLevelRoles_CanStillBeGranted_ByAnOwner()
    {
        Assert.Equal(RoleGrantProblem.None, TenantRoleGrantRules.Check(RoleOf("BuildingOwner", permissions: "[\"*\"]"), Tenant, TenantGrantor.Owner));
        Assert.Equal(RoleGrantProblem.None, TenantRoleGrantRules.Check(RoleOf("TenantAdmin", permissions: "[\"*\"]"), Tenant, TenantGrantor.Owner));
    }

    [Fact]
    public void CustomRoleOfThisTenant_IsFine_ButAnotherTenantsIsNot()
    {
        Assert.Equal(RoleGrantProblem.None, TenantRoleGrantRules.Check(RoleOf("Cleaner", tenantId: Tenant), Tenant, TenantGrantor.Manager));
        Assert.Equal(RoleGrantProblem.OtherTenantRole, TenantRoleGrantRules.Check(RoleOf("Cleaner", tenantId: Guid.NewGuid()), Tenant, TenantGrantor.Manager));
    }

    // ----- messages -----------------------------------------------------------------------------

    [Theory]
    [InlineData(RoleGrantProblem.FederatedAppRole, "SuperAdmin")]
    [InlineData(RoleGrantProblem.PrivilegedRole, "SuperAdmin")]
    public void Describe_GivesAPlainLanguageMessage(RoleGrantProblem problem, string mustMention)
    {
        var message = TenantRoleGrantRules.Describe(problem, "taskmanager:admin");

        Assert.Contains("taskmanager:admin", message);
        Assert.Contains(mustMention, message);
    }
}
