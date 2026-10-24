using Microsoft.AspNetCore.Authorization;

namespace IAM.API.Auth;

/// <summary>
/// Authorization requirement that demands a specific OAuth2 scope in the token (task 4059).
/// Works with OpenIddict at+jwt tokens where the scope claim is space-separated.
/// </summary>
public sealed class ScopeRequirement : IAuthorizationRequirement
{
    public string RequiredScope { get; }

    public ScopeRequirement(string scope) => RequiredScope = scope;
}

/// <summary>Handles <see cref="ScopeRequirement"/>.</summary>
public sealed class ScopeRequirementHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        // OpenIddict stores all granted scopes as a single space-separated "scope" claim
        // on the access token. Split and check membership.
        var granted = context.User
            .FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

        if (granted.Contains(requirement.RequiredScope))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
