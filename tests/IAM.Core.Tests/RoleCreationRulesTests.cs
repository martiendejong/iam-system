using IAM.Core.Services;

namespace IAM.Core.Tests;

/// <summary>Task 5163: what a BuildingOwner may put in a new role. The checks run in a fixed order; a SuperAdmin is never checked.</summary>
public class RoleCreationRulesTests
{
    private static readonly Guid Own = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> Owned = new HashSet<Guid> { Own };

    private static RoleCreateProblem Check(
        string name = "Floor manager",
        Guid? tenant = null,
        string? category = null,
        string[]? permissions = null,
        IReadOnlySet<Guid>? owned = null) =>
        RoleCreationRules.CheckForTenantOwner(name, tenant ?? Own, category, permissions ?? new[] { "buildings.read" }, owned ?? Owned);

    [Fact]
    public void APlainRoleInAnOwnedTenant_IsAllowed() =>
        Assert.Equal(RoleCreateProblem.None, Check());

    [Fact]
    public void NoPermissionsAtAll_IsAllowed() =>
        Assert.Equal(RoleCreateProblem.None, RoleCreationRules.CheckForTenantOwner("Reader", Own, null, null, Owned));

    [Fact]
    public void NoTenant_NeedsASuperAdmin() =>
        Assert.Equal(RoleCreateProblem.TenantRequired,
            RoleCreationRules.CheckForTenantOwner("Reader", null, null, null, Owned));

    [Fact]
    public void ATenantThatIsNotOwned_IsRefused() =>
        Assert.Equal(RoleCreateProblem.OtherTenant, Check(tenant: Other));

    [Fact]
    public void ACallerWhoOwnsNothing_CanCreateNothing() =>
        Assert.Equal(RoleCreateProblem.OtherTenant, Check(owned: new HashSet<Guid>()));

    [Theory]
    [InlineData("app:test-client")]
    [InlineData("APP:test-client")]
    [InlineData("  app:x ")]
    [InlineData("app:")]
    public void AnAppCategory_IsRefused(string category) =>
        Assert.Equal(RoleCreateProblem.AppCategory, Check(category: category));

    [Theory]
    [InlineData("Building")]
    [InlineData("application")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherCategories_AreAllowed(string? category) =>
        Assert.Equal(RoleCreateProblem.None, Check(category: category));

    [Theory]
    [InlineData("*")]
    [InlineData("users.*")]
    [InlineData("*.read")]
    [InlineData("acme:hq:*")]
    [InlineData("a*b")]
    public void AWildcardPermission_IsRefused(string permission) =>
        Assert.Equal(RoleCreateProblem.WildcardPermission, Check(permissions: new[] { "buildings.read", permission }));

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("systemadmin")]
    [InlineData(" SecurityAdmin ")]
    [InlineData("Admin")]
    public void APlatformRoleName_IsRefused(string name) =>
        Assert.Equal(RoleCreateProblem.PlatformRoleName, Check(name: name));

    [Fact]
    public void TheTenantIsCheckedBeforeTheOtherFields() =>
        Assert.Equal(RoleCreateProblem.OtherTenant,
            Check(name: "SuperAdmin", tenant: Other, category: "app:x", permissions: new[] { "*" }));

    [Fact]
    public void EveryProblemHasAMessage()
    {
        foreach (var problem in Enum.GetValues<RoleCreateProblem>().Where(p => p != RoleCreateProblem.None))
            Assert.False(string.IsNullOrWhiteSpace(RoleCreationRules.Describe(problem)), problem.ToString());
    }
}
