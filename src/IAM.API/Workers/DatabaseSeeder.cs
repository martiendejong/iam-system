using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace IAM.API.Workers;

/// <summary>
/// Seeds the database with test OAuth2 clients and scopes
/// </summary>
public class DatabaseSeeder : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseSeeder> _logger;
    private readonly IWebHostEnvironment _env;

    public DatabaseSeeder(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<DatabaseSeeder> logger, IWebHostEnvironment env)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _env = env;
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
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DatabaseSeeder>>();
        await context.Database.EnsureCreatedAsync(cancellationToken);

        await SeedScopesAsync(scope.ServiceProvider, cancellationToken);
        await SeedClientsAsync(scope.ServiceProvider, _configuration, _env, cancellationToken);
        await SeedAdminUserAsync(context, cancellationToken);

        // Security check: alert if test accounts exist in non-development environment
        if (!_env.IsDevelopment())
        {
            var testUsers = await context.Users.Where(u => u.Email.EndsWith("@test.com")).AnyAsync(cancellationToken);
            if (testUsers)
                _logger.LogCritical("SECURITY ALERT: Test accounts (@test.com) found in non-development environment!");
        }

        await AuditAdminRolesAsync(context, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Startup audit: logs all users with SuperAdmin or Admin role and warns about any
    /// that are not in the approved admin allowlist. Does NOT remove or change any roles.
    /// </summary>
    private async Task AuditAdminRolesAsync(IAMDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            var adminUsers = await context.UserRoles
                .Include(ur => ur.Role)
                .Include(ur => ur.User)
                .Where(ur => ur.Role.Name == "SuperAdmin" || ur.Role.Name == "Admin" || ur.Role.Name == "admin")
                .Select(ur => new { ur.User.Email, ur.User.Id, RoleName = ur.Role.Name })
                .ToListAsync(cancellationToken);

            // Approved admin accounts — only these may hold SuperAdmin/Admin roles
            var allowedAdmins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "info@martiendejong.nl",      // Martien de Jong (owner)
                "frankobaai@gmail.com",        // Frank
                "mpoelessy839@gmail.com",      // Lessy (Lesiamon, Sofy's son)
                "mpoesimitia@gmail.com",       // Sandra
            };

            foreach (var admin in adminUsers)
            {
                if (!allowedAdmins.Contains(admin.Email ?? ""))
                    _logger.LogWarning(
                        "AdminRoleAudit: User {Email} (Id: {Id}) has role '{Role}' but is NOT in the approved admin allowlist. " +
                        "Review and revoke if not authorized.",
                        admin.Email, admin.Id, admin.RoleName);
                else
                    _logger.LogInformation(
                        "AdminRoleAudit: Approved admin {Email} (Id: {Id}) has role '{Role}'.",
                        admin.Email, admin.Id, admin.RoleName);
            }

            _logger.LogInformation("AdminRoleAudit: Complete. Found {Count} admin user(s) in total.", adminUsers.Count);
        }
        catch (Exception ex)
        {
            // Audit failure must never crash startup
            _logger.LogError(ex, "AdminRoleAudit: Failed to complete admin role audit.");
        }
    }

    private async Task SeedAdminUserAsync(IAMDbContext context, CancellationToken cancellationToken)
    {
        var email = _configuration["AdminSeed:Email"];
        var password = _configuration["AdminSeed:Password"];
        var firstName = _configuration["AdminSeed:FirstName"] ?? "Admin";
        var lastName = _configuration["AdminSeed:LastName"] ?? "User";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return;

        if (await context.Users.AnyAsync(u => u.Email == email, cancellationToken)) return;

        // "SuperAdmin" is the system's apex role (seeded via EF migration HasData with a
        // well-known Id) and the only role every [Authorize(Roles=...)] admin endpoint
        // ultimately accepts, directly or via SuperAdminClaimsTransformation. A bare "Admin"
        // role - which this method used to create - isn't referenced by any authorization
        // policy, so a user seeded with it gets 403s on Users/OAuth Clients/etc.
        var superAdminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "SuperAdmin", cancellationToken);
        if (superAdminRole == null)
        {
            superAdminRole = new Role
            {
                Id = Guid.NewGuid(),
                Name = "SuperAdmin",
                Description = "Full system access",
                IsSystemRole = true,
                Permissions = "[\"*\"]",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            context.Roles.Add(superAdminRole);
        }

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
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
            RoleId = superAdminRole.Id,
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

    private static async Task SeedClientsAsync(IServiceProvider provider, IConfiguration configuration, IWebHostEnvironment env, CancellationToken cancellationToken)
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

        // Test Client #2: Postman/Testing (Authorization Code Flow) — development only
        if (env.IsDevelopment() && await manager.FindByClientIdAsync("postman_client", cancellationToken) == null)
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

        // Password Manager — vault.prospergenics.com (Authorization Code Flow with PKCE, public client)
        if (await manager.FindByClientIdAsync("passwordmanager", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "passwordmanager",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Password Manager",
                RedirectUris =
                {
                    new Uri("https://vault.prospergenics.com/api/auth/iam/callback"),
                    new Uri("http://localhost:5076/api/auth/iam/callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://vault.prospergenics.com/"),
                    new Uri("http://localhost:5174/")
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

        // Test Client #4: Backend Service (Client Credentials Flow) — development only
        if (env.IsDevelopment() && await manager.FindByClientIdAsync("backend_service", cancellationToken) == null)
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
                ClientSecret = configuration["Clients:OpenWebUi:Secret"] ?? throw new InvalidOperationException("Clients:OpenWebUi:Secret not configured"),
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

        // Yin Yoga Sound Coach App (Authorization Code Flow with PKCE, public client)
        if (await manager.FindByClientIdAsync("coach-app", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "coach-app",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Yin Yoga Sound Coach App",
                RedirectUris =
                {
                    new Uri("http://localhost:5210/api/auth/iam/callback"),
                    new Uri("https://tripplanner.sprout2grow.com/yys-demo/api/auth/iam/callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("http://localhost:5210"),
                    new Uri("https://tripplanner.sprout2grow.com")
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

        // Jengo AGI Dashboard (Authorization Code Flow with PKCE, public client)
        if (await manager.FindByClientIdAsync("jengo-agi", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "jengo-agi",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Jengo AGI Dashboard",
                RedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/jengo-agi/signin-oidc"),
                    new Uri("http://localhost:8199/signin-oidc"),
                    new Uri("https://maendeleo.martiendejong.nl/signin-oidc"),
                    new Uri("https://workspace.artrevisionist.com/signin-oidc")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("http://localhost:8199/"),
                    new Uri("https://maendeleo.martiendejong.nl/jengo-agi/"),
                    new Uri("https://workspace.artrevisionist.com/jengo-agi/")
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
                    $"{OpenIddictConstants.Permissions.Prefixes.Scope}{OpenIddictConstants.Scopes.Email}"
                },
                Requirements =
                {
                    OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
                }
            }, cancellationToken);
        }

        // Jengo Meeting Assistant (Authorization Code Flow with PKCE, public client)
        if (await manager.FindByClientIdAsync("jengo-meeting", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "jengo-meeting",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Jengo Meeting Assistant",
                RedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/meeting/api/auth/callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/meeting/")
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

        // Jengo Workspace (artrevisionist) (Authorization Code Flow with PKCE, public client)
        if (await manager.FindByClientIdAsync("jengo-workspace", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "jengo-workspace",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Jengo Workspace (artrevisionist)",
                RedirectUris =
                {
                    new Uri("https://workspace.artrevisionist.com/api/auth/iam/callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://workspace.artrevisionist.com/")
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

        // Jengo VPS MCP Server — confidential server-to-server OAuth client.
        // The MCP server uses this to exchange IAM auth codes for ID tokens
        // (server-side, no browser involvement). ClientSecret must match
        // IamOidc:ClientSecret in the MCP server's appsettings on each host.
        if (await manager.FindByClientIdAsync("jengo-vps-mcp", cancellationToken) == null)
        {
            var iamClientSecret = configuration["JengoVpsMcp:ClientSecret"]
                ?? (env.IsDevelopment()
                    ? "jengo-mcp-iam-secret-dev"
                    : throw new InvalidOperationException("JengoVpsMcp:ClientSecret not configured in production"));
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "jengo-vps-mcp",
                ClientSecret = iamClientSecret,
                ClientType = OpenIddictConstants.ClientTypes.Confidential,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Jengo VPS MCP Server",
                RedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/oauth/iam-callback"),
                    new Uri("https://therealm.martiendejong.nl/oauth/iam-callback"),
                    new Uri("http://localhost:5100/oauth/iam-callback")
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

        // Sinema — AI video editor (Authorization Code Flow with PKCE, public client)
        // Prod runs behind the /sinema path base on maendeleo; local dev uses the Vite
        // dev server (5311, /api proxied to the backend) or the backend directly (5310).
        if (await manager.FindByClientIdAsync("sinema", cancellationToken) == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "sinema",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                DisplayName = "Sinema",
                RedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/sinema/api/auth/iam/callback"),
                    new Uri("http://localhost:5311/api/auth/iam/callback"),
                    new Uri("http://localhost:5310/api/auth/iam/callback")
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://maendeleo.martiendejong.nl/sinema/"),
                    new Uri("http://localhost:5311/"),
                    new Uri("http://localhost:5310/")
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
    }
}
