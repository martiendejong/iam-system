using System.Linq.Expressions;
using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// The one rule for "is this role assignment in force right now": a <see cref="UserRole"/> with no
/// <see cref="UserRole.ExpiresAt"/> never lapses, one with an expiry counts only while that moment is
/// still in the future (the expiry instant itself is already over). Every place that turns a user's
/// roles into something a caller can act on - JWT and OIDC role claims, the userinfo response, the
/// app sign-in check, the claims-mapping preview - goes through this class (task 5156), so a
/// time-boxed role such as a temporary SuperAdmin really ends instead of being written into every
/// login and refresh. The rule is written once, as an expression, so the in-memory and the database
/// form cannot drift apart.
/// </summary>
public static class UserRoleActivity
{
    /// <summary>The rule itself, as an expression EF Core can translate to SQL.</summary>
    public static Expression<Func<UserRole, bool>> ActiveAt(DateTime utcNow) =>
        ur => ur.ExpiresAt == null || ur.ExpiresAt > utcNow;

    /// <summary>Role assignments in force at <paramref name="utcNow"/> (database-side filter).</summary>
    public static IQueryable<UserRole> WhereActive(this IQueryable<UserRole> userRoles, DateTime utcNow) =>
        userRoles.Where(ActiveAt(utcNow));

    /// <summary>Role assignments in force right now (database-side filter).</summary>
    public static IQueryable<UserRole> WhereActive(this IQueryable<UserRole> userRoles) =>
        userRoles.WhereActive(DateTime.UtcNow);

    /// <summary>Role assignments in force at <paramref name="utcNow"/> (for already loaded assignments).</summary>
    public static IEnumerable<UserRole> WhereActive(this IEnumerable<UserRole> userRoles, DateTime utcNow) =>
        userRoles.AsQueryable().Where(ActiveAt(utcNow));

    /// <summary>Role assignments in force right now (for already loaded assignments).</summary>
    public static IEnumerable<UserRole> WhereActive(this IEnumerable<UserRole> userRoles) =>
        userRoles.WhereActive(DateTime.UtcNow);
}
