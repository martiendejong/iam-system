using System.IdentityModel.Tokens.Jwt;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.API.Tests.Infrastructure;

/// <summary>
/// Test web application factory for integration testing
/// Uses in-memory database for isolated test execution
/// </summary>
public class IAMTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private bool _seeded = false;

    public IAMTestWebApplicationFactory()
    {
        // Clear default JWT claim type mappings that ASP.NET Core applies
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
        JwtSecurityTokenHandler.DefaultOutboundClaimTypeMap.Clear();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set environment to Development to load appsettings.Development.json
        builder.UseEnvironment("Development");

        // Configure test settings (JWT, etc.) - this overrides any existing config
        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "DEVELOPMENT_SECRET_KEY_CHANGE_IN_PRODUCTION_32_CHARS_MIN",
                ["Jwt:Issuer"] = "https://localhost:5001",
                ["Jwt:Audience"] = "iam-api"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Remove PostgreSQL DbContext and all related registrations
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<IAMDbContext>) ||
                           d.ServiceType == typeof(IAMDbContext) ||
                           d.ServiceType.Name.Contains("DbContext"))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            // Remove background workers that might interfere with testing
            var hostedServices = services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
            foreach (var service in hostedServices)
            {
                services.Remove(service);
            }

            // Configure JWT Bearer options for testing
            services.Configure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    // Map role claims correctly
                    options.TokenValidationParameters.RoleClaimType = System.Security.Claims.ClaimTypes.Role;
                    options.TokenValidationParameters.NameClaimType = System.Security.Claims.ClaimTypes.Name;

                    // Explicitly set validation parameters to ensure they match test tokens
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.ValidateLifetime = true;
                    options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;

                    // Add event handlers for debugging
                    options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
                    {
                        OnAuthenticationFailed = context =>
                        {
                            System.Console.WriteLine($"JWT Auth Failed: {context.Exception.Message}");
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = context =>
                        {
                            System.Console.WriteLine($"JWT Token Validated: {context.Principal?.Identity?.Name}");
                            return Task.CompletedTask;
                        },
                        OnChallenge = context =>
                        {
                            System.Console.WriteLine($"JWT Challenge: {context.Error}, {context.ErrorDescription}");
                            return Task.CompletedTask;
                        }
                    };
                });

            // Configure authentication to use JWT Bearer as default for tests
            services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
            {
                options.DefaultScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
                options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
            });

            // Configure authorization to use JWT Bearer scheme
            services.Configure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(options =>
            {
                options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build();
            });

            // Add InMemory DbContext
            services.AddDbContext<IAMDbContext>(options =>
            {
                options.UseInMemoryDatabase("IAMTestDb");
            });

            // Seed database immediately after services are built
            var sp = services.BuildServiceProvider();
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
                db.Database.EnsureCreated();

                if (!db.Tenants.Any())
                {
                    SeedTestData(db);
                    db.SaveChanges();
                }
            }

            _seeded = true;
        });
    }

    public new HttpClient CreateClient()
    {
        return CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public async Task EnsureSeededAsync()
    {
        if (_seeded) return;

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();

        await db.Database.EnsureCreatedAsync();

        if (!db.Tenants.Any())
        {
            SeedTestData(db);
            await db.SaveChangesAsync();
        }

        _seeded = true;
    }

    private void SeedTestData(IAMDbContext context)
    {
        // Create test tenant
        var rootTenant = new Tenant
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Root Tenant",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        context.Tenants.Add(rootTenant);

        // Create test roles (all roles needed by tests)
        var systemAdminRole = new Role
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Name = "SystemAdmin",
            Description = "System Administrator",
            TenantId = rootTenant.Id,
            CreatedAt = DateTime.UtcNow
        };

        var tenantAdminRole = new Role
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01"),
            Name = "TenantAdmin",
            Description = "Tenant Administrator",
            TenantId = rootTenant.Id,
            CreatedAt = DateTime.UtcNow
        };

        var securityAdminRole = new Role
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa02"),
            Name = "SecurityAdmin",
            Description = "Security Administrator",
            TenantId = rootTenant.Id,
            CreatedAt = DateTime.UtcNow
        };

        var emergencyAccessRole = new Role
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa03"),
            Name = "EmergencyAccess",
            Description = "Emergency Access",
            TenantId = rootTenant.Id,
            CreatedAt = DateTime.UtcNow
        };

        var complianceOfficerRole = new Role
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa04"),
            Name = "ComplianceOfficer",
            Description = "Compliance Officer",
            TenantId = rootTenant.Id,
            CreatedAt = DateTime.UtcNow
        };

        var userRole = new Role
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Name = "User",
            Description = "Standard User",
            TenantId = rootTenant.Id,
            CreatedAt = DateTime.UtcNow
        };

        context.Roles.AddRange(systemAdminRole, tenantAdminRole, securityAdminRole,
            emergencyAccessRole, complianceOfficerRole, userRole);

        // Create test users
        var adminUser = new User
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            Email = "admin@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test123!")
        };

        var testUser = new User
        {
            Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            Email = "user@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test123!")
        };

        context.Users.AddRange(adminUser, testUser);

        // Assign roles to users
        var adminUserRoles = new[]
        {
            new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = adminUser.Id,
                RoleId = systemAdminRole.Id,
                TenantId = rootTenant.Id,
                GrantedAt = DateTime.UtcNow
            },
            new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = adminUser.Id,
                RoleId = tenantAdminRole.Id,
                TenantId = rootTenant.Id,
                GrantedAt = DateTime.UtcNow
            },
            new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = adminUser.Id,
                RoleId = securityAdminRole.Id,
                TenantId = rootTenant.Id,
                GrantedAt = DateTime.UtcNow
            },
            new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = adminUser.Id,
                RoleId = emergencyAccessRole.Id,
                TenantId = rootTenant.Id,
                GrantedAt = DateTime.UtcNow
            },
            new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = adminUser.Id,
                RoleId = complianceOfficerRole.Id,
                TenantId = rootTenant.Id,
                GrantedAt = DateTime.UtcNow
            }
        };

        var testUserRole = new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = testUser.Id,
            RoleId = userRole.Id,
            TenantId = rootTenant.Id,
            GrantedAt = DateTime.UtcNow
        };

        context.UserRoles.AddRange(adminUserRoles);
        context.UserRoles.Add(testUserRole);

        // Create test policy
        var testPolicy = new Policy
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Name = "Test Policy",
            Description = "Policy for testing",
            TenantId = rootTenant.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        context.Policies.Add(testPolicy);
    }
}
