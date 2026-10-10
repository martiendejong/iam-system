using IAM.Core.Entities;
using IAM.Core.Services;

namespace IAM.Core.Tests;

/// <summary>
/// Task 5156: the one "is this role assignment in force now" rule. A role with no expiry never lapses,
/// one with a future expiry is in force, one whose expiry has passed (or is exactly now) is not.
/// The in-memory and the queryable form are the same expression, so they must answer identically.
/// </summary>
public class UserRoleActivityTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private static UserRole AssignmentExpiring(DateTime? expiresAt) =>
        new() { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RoleId = Guid.NewGuid(), ExpiresAt = expiresAt };

    [Fact]
    public void NoExpiry_IsActive()
    {
        var assignment = AssignmentExpiring(null);

        Assert.Single(new[] { assignment }.WhereActive(Now));
    }

    [Fact]
    public void ExpiryInTheFuture_IsActive()
    {
        var assignment = AssignmentExpiring(Now.AddSeconds(1));

        Assert.Single(new[] { assignment }.WhereActive(Now));
    }

    [Fact]
    public void ExpiryInThePast_IsNotActive()
    {
        var assignment = AssignmentExpiring(Now.AddSeconds(-1));

        Assert.Empty(new[] { assignment }.WhereActive(Now));
    }

    [Fact]
    public void ExpiryExactlyNow_IsNotActive_TheExpiryInstantIsAlreadyOver()
    {
        var assignment = AssignmentExpiring(Now);

        Assert.Empty(new[] { assignment }.WhereActive(Now));
    }

    [Fact]
    public void WhereActive_KeepsOnlyTheAssignmentsInForce_AndTheirOrder()
    {
        var forever = AssignmentExpiring(null);
        var lapsed = AssignmentExpiring(Now.AddDays(-3));
        var later = AssignmentExpiring(Now.AddDays(3));

        var active = new[] { forever, lapsed, later }.WhereActive(Now).ToList();

        Assert.Equal(new[] { forever, later }, active);
    }

    [Fact]
    public void QueryableAndInMemoryForms_AgreeOnEveryCase()
    {
        var assignments = new[]
        {
            AssignmentExpiring(null),
            AssignmentExpiring(Now.AddDays(-1)),
            AssignmentExpiring(Now.AddMilliseconds(-1)),
            AssignmentExpiring(Now),
            AssignmentExpiring(Now.AddMilliseconds(1)),
            AssignmentExpiring(Now.AddDays(1)),
        };

        var inMemory = ((IEnumerable<UserRole>)assignments).WhereActive(Now).ToList();
        var queryable = assignments.AsQueryable().WhereActive(Now).ToList();

        Assert.Equal(inMemory, queryable);
        Assert.Equal(3, inMemory.Count);
    }

    [Fact]
    public void WhereActiveWithoutAnInstant_UsesTheClockNow()
    {
        var lapsed = AssignmentExpiring(DateTime.UtcNow.AddMinutes(-5));
        var running = AssignmentExpiring(DateTime.UtcNow.AddMinutes(5));

        var active = new[] { lapsed, running }.WhereActive().ToList();

        Assert.Equal(new[] { running }, active);
    }
}
