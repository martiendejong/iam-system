using IAM.Core.Entities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace IAM.Infrastructure.Data;

public class IAMDbContext : DbContext
{
    public IAMDbContext(DbContextOptions<IAMDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<ScheduleTemplate> ScheduleTemplates => Set<ScheduleTemplate>();
    public DbSet<HolidayCalendar> HolidayCalendars => Set<HolidayCalendar>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<TemporaryAccessGrant> TemporaryAccessGrants => Set<TemporaryAccessGrant>();
    public DbSet<PolicyAuditEvent> PolicyAuditEvents => Set<PolicyAuditEvent>();
    public DbSet<ComplianceReport> ComplianceReports => Set<ComplianceReport>();
    public DbSet<PolicyTest> PolicyTests => Set<PolicyTest>();
    public DbSet<PolicyTestResult> PolicyTestResults => Set<PolicyTestResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure OpenIddict to use PostgreSQL (stores: applications, authorizations, scopes, tokens)
        modelBuilder.UseOpenIddict();

        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(255);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PhoneNumber).HasMaxLength(50);

            entity.HasMany(e => e.UserRoles)
                .WithOne(e => e.User)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.RefreshTokens)
                .WithOne(e => e.User)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.AuditLogs)
                .WithOne(e => e.User)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Tenant configuration
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("Tenants");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
            entity.Property(e => e.Settings).HasColumnType("jsonb");

            entity.HasOne(e => e.ParentTenant)
                .WithMany(e => e.ChildTenants)
                .HasForeignKey(e => e.ParentTenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Roles)
                .WithOne(e => e.Tenant)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Role configuration
        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("Roles");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.Name });
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Permissions).HasColumnType("jsonb");

            entity.HasMany(e => e.UserRoles)
                .WithOne(e => e.Role)
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // UserRole configuration
        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.ToTable("UserRoles");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.RoleId, e.TenantId }).IsUnique();

            entity.HasOne(e => e.Tenant)
                .WithMany(e => e.UserRoles)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // RefreshToken configuration
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(255);
        });

        // AuditLog configuration
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.Action, e.Resource });
            entity.Property(e => e.Action).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Resource).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Details).HasColumnType("jsonb");
        });

        // Policy configuration
        modelBuilder.Entity<Policy>(entity =>
        {
            entity.ToTable("Policies");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Resource).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Action).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TimeConstraints).HasColumnType("jsonb");
            entity.Property(e => e.Conditions).HasColumnType("jsonb");

            // Indexes for performance
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.RoleId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.Resource, e.Action });
            entity.HasIndex(e => e.InheritedFromPolicyId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive, e.ExpiresAt });

            // Relationships
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Role)
                .WithMany()
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.InheritedFromPolicy)
                .WithMany(e => e.InheritedPolicies)
                .HasForeignKey(e => e.InheritedFromPolicyId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ScheduleTemplate)
                .WithMany(e => e.Policies)
                .HasForeignKey(e => e.ScheduleTemplateId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ScheduleTemplate configuration
        modelBuilder.Entity<ScheduleTemplate>(entity =>
        {
            entity.ToTable("ScheduleTemplates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ScheduleType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Timezone).IsRequired().HasMaxLength(100);
            entity.Property(e => e.RecurrencePattern).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.ExceptionDates).HasColumnType("jsonb");
            entity.Property(e => e.InclusionDates).HasColumnType("jsonb");

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.ScheduleType, e.IsActive });

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // HolidayCalendar configuration
        modelBuilder.Entity<HolidayCalendar>(entity =>
        {
            entity.ToTable("HolidayCalendars");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Timezone).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CountryCode).HasMaxLength(2);
            entity.Property(e => e.Holidays).IsRequired().HasColumnType("jsonb");

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CountryCode);
            entity.HasIndex(e => new { e.IsActive, e.IsAutoUpdated });

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // MaintenanceWindow configuration
        modelBuilder.Entity<MaintenanceWindow>(entity =>
        {
            entity.ToTable("MaintenanceWindows");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Timezone).IsRequired().HasMaxLength(100);
            entity.Property(e => e.RecurrencePattern).HasMaxLength(500);

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.Status, e.StartTime, e.EndTime });
            entity.HasIndex(e => e.StartTime);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TemporaryAccessGrant configuration
        modelBuilder.Entity<TemporaryAccessGrant>(entity =>
        {
            entity.ToTable("TemporaryAccessGrants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Justification).IsRequired().HasMaxLength(1000);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.RoleId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.Status, e.StartTime, e.EndTime });
            entity.HasIndex(e => new { e.UserId, e.Status, e.EndTime });

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Role)
                .WithMany()
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PolicyAuditEvent configuration
        modelBuilder.Entity<PolicyAuditEvent>(entity =>
        {
            entity.ToTable("PolicyAuditEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Resource).HasMaxLength(200);
            entity.Property(e => e.Action).HasMaxLength(100);
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.Changes).HasColumnType("jsonb");
            entity.Property(e => e.Metadata).HasColumnType("jsonb");

            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
            entity.HasIndex(e => new { e.PolicyId, e.CreatedAt });
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
            entity.HasIndex(e => new { e.EventType, e.CreatedAt });
            entity.HasIndex(e => e.TriggeredAlert);

            entity.HasOne(e => e.Policy)
                .WithMany()
                .HasForeignKey(e => e.PolicyId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ComplianceReport configuration
        modelBuilder.Entity<ComplianceReport>(entity =>
        {
            entity.ToTable("ComplianceReports");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Framework).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Findings).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.Recommendations).HasColumnType("jsonb");
            entity.Property(e => e.Statistics).IsRequired().HasColumnType("jsonb");

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.Framework, e.Status });
            entity.HasIndex(e => new { e.PeriodStart, e.PeriodEnd });
            entity.HasIndex(e => e.GeneratedAt);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PolicyTest configuration
        modelBuilder.Entity<PolicyTest>(entity =>
        {
            entity.ToTable("PolicyTests");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.DraftPolicyJson).HasColumnType("jsonb");
            entity.Property(e => e.TestScenarios).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.ExpectedResults).IsRequired().HasColumnType("jsonb");

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.PolicyId);
            entity.HasIndex(e => e.IsActive);

            entity.HasOne(e => e.Policy)
                .WithMany()
                .HasForeignKey(e => e.PolicyId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PolicyTestResult configuration
        modelBuilder.Entity<PolicyTestResult>(entity =>
        {
            entity.ToTable("PolicyTestResults");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DetailedResults).IsRequired().HasColumnType("jsonb");

            entity.HasIndex(e => e.PolicyTestId);
            entity.HasIndex(e => e.ExecutedAt);
            entity.HasIndex(e => new { e.Status, e.ExecutedAt });

            entity.HasOne(e => e.PolicyTest)
                .WithMany(e => e.TestResults)
                .HasForeignKey(e => e.PolicyTestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Seed system roles
        SeedSystemRoles(modelBuilder);
    }

    private void SeedSystemRoles(ModelBuilder modelBuilder)
    {
        var superAdminId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var buildingOwnerId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var buildingManagerId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var realEstateAgentId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var contractorId = Guid.Parse("00000000-0000-0000-0000-000000000005");
        var residentId = Guid.Parse("00000000-0000-0000-0000-000000000006");

        modelBuilder.Entity<Role>().HasData(
            new Role
            {
                Id = superAdminId,
                Name = "SuperAdmin",
                Description = "Full system access",
                IsSystemRole = true,
                Permissions = @"[""*""]",
                CreatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc)
            },
            new Role
            {
                Id = buildingOwnerId,
                Name = "BuildingOwner",
                Description = "Building owner with full building access",
                IsSystemRole = true,
                Permissions = @"[""Building.View"",""Building.Manage"",""Floor.View"",""Floor.Manage"",""Room.View"",""Room.Manage"",""Device.View"",""Device.Control"",""User.Invite"",""User.ViewBuilding""]",
                CreatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc)
            },
            new Role
            {
                Id = buildingManagerId,
                Name = "BuildingManager",
                Description = "Building manager with operational access",
                IsSystemRole = true,
                Permissions = @"[""Building.View"",""Floor.View"",""Floor.Manage"",""Room.View"",""Room.Manage"",""Device.View"",""Device.Control"",""Maintenance.Schedule""]",
                CreatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc)
            },
            new Role
            {
                Id = realEstateAgentId,
                Name = "RealEstateAgent",
                Description = "Real estate agent for property listings and showings",
                IsSystemRole = true,
                Permissions = @"[""Property.List"",""Property.View"",""Property.Manage"",""Showing.Schedule"",""Client.Manage""]",
                CreatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc)
            },
            new Role
            {
                Id = contractorId,
                Name = "Contractor",
                Description = "Contractor with time-limited maintenance access",
                IsSystemRole = true,
                Permissions = @"[""Building.View"",""Room.View"",""Device.View"",""Maintenance.Perform"",""WorkOrder.View""]",
                CreatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc)
            },
            new Role
            {
                Id = residentId,
                Name = "Resident",
                Description = "Building resident/tenant",
                IsSystemRole = true,
                Permissions = @"[""Room.View"",""Device.ViewOwn"",""Amenity.Book"",""Maintenance.Request""]",
                CreatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc)
            }
        );
    }
}
