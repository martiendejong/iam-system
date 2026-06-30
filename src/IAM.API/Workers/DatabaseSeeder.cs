using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;

namespace IAM.API.Workers;

/// <summary>
/// Seeds the database with test OAuth2 clients and scopes
/// </summary>
public class DatabaseSeeder : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public DatabaseSeeder(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget so we don't block the Windows SCM startup timeout (30s)
        _ = Task.Run(() => InitializeDatabaseAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        await context.Database.EnsureCreatedAsync(cancellationToken);

        await SeedScopesAsync(scope.ServiceProvider, cancellationToken);
        await SeedClientsAsync(scope.ServiceProvider, cancellationToken);
        await SeedAdminUserAsync(context, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedAdminUserAsync(IAMDbContext context, CancellationToken cancellationToken)
    {
        var email = _configuration["AdminSeed:Email"];
        var password = _configuration["AdminSeed:Password"];
        var firstName = _configuration["AdminSeed:FirstName"] ?? "Admin";
        var lastName = _configuration["AdminSeed:LastName"] ?? "User";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return;

        if (await context.Users.AnyAsync(u => u.Email == email, cancellationToken)) return;

        var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "Admin", cancellationToken);
        if (adminRole == null)
        {
            adminRole = new Role
            {
                Id = Guid.NewGuid(),
                Name = "Admin",
                Description = "Full system administrator access",
                IsSystemRole = true,
                Permissions = "[\"*\"]",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            context.Roles.Add(adminRole);

            var userRole = new Role
            {
                Id = Guid.NewGuid(),
                Name = "User",
                Description = "Standard user access",
                IsSystemRole = true,
                Permissions = "[\"User.View\", \"User.Update\"]",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            context.Roles.Add(userRole);
        }

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FirstName = firstName,
            LastName = lastName,
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(admin);

        context.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = adminRole.Id,
            GrantedAt = DateTime.UtcNow
        });

        await context.SaveChangesAsync(cancellationToken);
    }

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

        // Jengo Web (Authorization Code Flow with PKCE)
        if (await manager.FindByClientIdAsync("jengo-web", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "jengo-web",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Jengo Web",
                RedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/jengo/api/auth/callback"),
                    new Uri("http://localhost:3220/api/auth/callback"),
                    new Uri("http://localhost:3001/api/auth/callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/jengo/"),
                    new Uri("http://localhost:3220/"),
                    new Uri("http://localhost:3001/")
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
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Roles}"
                },
                Requirements =
                {
                    OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
                }
            }, cancellationToken);
        }

        // Port of Giethoorn — intake.sprout2grow.com (Authorization Code Flow with PKCE, public client)
        if (await manager.FindByClientIdAsync("portofgiethoorn", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "portofgiethoorn",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Port of Giethoorn (intake)",
                RedirectUris =
                {
                    new Uri("https://intake.sprout2grow.com/api/auth/iam/callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://intake.sprout2grow.com/")
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
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Roles}"
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

        // Open WebUI (Authorization Code Flow with client secret)
        if (await manager.FindByClientIdAsync("open-webui", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "open-webui",
                ClientSecret = "Ow9kP2mXqR5vN8dL3jT7",
                ClientType = OpenIddictConstants.ClientTypes.Confidential,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Open WebUI",
                RedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/oauth/oidc/callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/")
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
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Roles}"
                }
            }, cancellationToken);
        }
    }
}
