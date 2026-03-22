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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Configure test settings (JWT, etc.)
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
            // Remove PostgreSQL DbContext
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<IAMDbContext>));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            // Remove background workers that might interfere with testing
            var hostedServices = services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
            foreach (var service in hostedServices)
            {
                services.Remove(service);
            }

            // Add InMemory DbContext
            services.AddDbContext<IAMDbContext>(options =>
            {
                options.UseInMemoryDatabase("IAMTestDb");
            });
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

        // Create test roles
        var adminRole = new Role
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Name = "SystemAdmin",
            Description = "System Administrator",
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

        context.Roles.AddRange(adminRole, userRole);

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
