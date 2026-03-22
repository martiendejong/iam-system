using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace IAM.API.Workers;

/// <summary>
/// Seeds the database with test OAuth2 clients and scopes
/// </summary>
public class DatabaseSeeder : IHostedService
{
    private readonly IServiceProvider _serviceProvider;

    public DatabaseSeeder(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        await context.Database.EnsureCreatedAsync(cancellationToken);

        await SeedScopesAsync(scope.ServiceProvider, cancellationToken);
        await SeedClientsAsync(scope.ServiceProvider, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SeedScopesAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var manager = provider.GetRequiredService<IOpenIddictScopeManager>();

        // OpenID Connect standard scopes
        if (await manager.FindByNameAsync(OpenIddictConstants.Scopes.OpenId, cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = OpenIddictConstants.Scopes.OpenId,
                DisplayName = "OpenID",
                Description = "OpenID Connect authentication",
                Resources = { "iam_api" }
            }, cancellationToken);
        }

        if (await manager.FindByNameAsync(OpenIddictConstants.Scopes.Profile, cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = OpenIddictConstants.Scopes.Profile,
                DisplayName = "Profile",
                Description = "Access to user profile information",
                Resources = { "iam_api" }
            }, cancellationToken);
        }

        if (await manager.FindByNameAsync(OpenIddictConstants.Scopes.Email, cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = OpenIddictConstants.Scopes.Email,
                DisplayName = "Email",
                Description = "Access to user email address",
                Resources = { "iam_api" }
            }, cancellationToken);
        }

        if (await manager.FindByNameAsync(OpenIddictConstants.Scopes.Roles, cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = OpenIddictConstants.Scopes.Roles,
                DisplayName = "Roles",
                Description = "Access to user roles",
                Resources = { "iam_api" }
            }, cancellationToken);
        }

        // Custom scope for tenant access
        if (await manager.FindByNameAsync("tenants", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = "tenants",
                DisplayName = "Tenants",
                Description = "Access to user's tenant assignments",
                Resources = { "iam_api" }
            }, cancellationToken);
        }
    }

    private static async Task SeedClientsAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var manager = provider.GetRequiredService<IOpenIddictApplicationManager>();

        // Test Client #1: React Admin UI (Authorization Code Flow with PKCE)
        if (await manager.FindByClientIdAsync("react_admin_ui", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "react_admin_ui",
                ClientType = OpenIddictConstants.ClientTypes.Public, // Public client (no secret)
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit, // Auto-consent for demo
                DisplayName = "React Admin UI",
                RedirectUris =
                {
                    new Uri("http://localhost:5173/callback"),
                    new Uri("https://localhost:5173/callback"),
                    new Uri("http://localhost:5173/silent-renew"),
                    new Uri("https://localhost:5173/silent-renew")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("http://localhost:5173/"),
                    new Uri("https://localhost:5173/")
                },
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.OpenId}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Profile}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Email}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Roles}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}tenants"
                },
                Requirements =
                {
                    OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
                }
            }, cancellationToken);
        }

        // Test Client #2: Postman/Testing (Authorization Code Flow)
        if (await manager.FindByClientIdAsync("postman_client", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "postman_client",
                ClientSecret = "postman_secret_dev_only",
                ClientType = OpenIddictConstants.ClientTypes.Confidential, // Confidential client (has secret)
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Postman Testing Client",
                RedirectUris =
                {
                    new Uri("https://oauth.pstmn.io/v1/callback"),
                    new Uri("http://localhost:3000/callback")
                },
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.Endpoints.Introspection,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.OpenId}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Profile}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Email}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Roles}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}tenants"
                },
                Requirements =
                {
                    OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
                }
            }, cancellationToken);
        }

        // Test Client #3: Mobile App (Authorization Code Flow with PKCE)
        if (await manager.FindByClientIdAsync("mobile_app", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "mobile_app",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Mobile App",
                RedirectUris =
                {
                    new Uri("com.iam.mobile://callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("com.iam.mobile://logout")
                },
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.OpenId}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Profile}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Email}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Roles}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}tenants"
                },
                Requirements =
                {
                    OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
                }
            }, cancellationToken);
        }

        // Test Client #4: Backend Service (Client Credentials Flow)
        if (await manager.FindByClientIdAsync("backend_service", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "backend_service",
                ClientSecret = "backend_secret_dev_only",
                ClientType = OpenIddictConstants.ClientTypes.Confidential,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Backend Service",
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.Endpoints.Introspection,
                    OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.OpenId}",
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}tenants"
                }
            }, cancellationToken);
        }
    }
}
