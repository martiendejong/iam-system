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
    public DbSet<EmergencyOverride> EmergencyOverrides => Set<EmergencyOverride>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCertificate> DeviceCertificates => Set<DeviceCertificate>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<GroupRole> GroupRoles => Set<GroupRole>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<TelemetryRecord> TelemetryRecords => Set<TelemetryRecord>();
    public DbSet<IdentityProvider> IdentityProviders => Set<IdentityProvider>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<MagicLinkToken> MagicLinkTokens => Set<MagicLinkToken>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<DataRequest> DataRequests => Set<DataRequest>();
    public DbSet<DataProcessingAgreement> DataProcessingAgreements => Set<DataProcessingAgreement>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    public DbSet<DirectorySyncConfig> DirectorySyncConfigs => Set<DirectorySyncConfig>();
    public DbSet<DirectorySyncLog> DirectorySyncLogs => Set<DirectorySyncLog>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<WorkflowTemplate> WorkflowTemplates => Set<WorkflowTemplate>();
    public DbSet<ScimProvisioningLog> ScimProvisioningLogs => Set<ScimProvisioningLog>();
    public DbSet<ScimToken> ScimTokens => Set<ScimToken>();
    public DbSet<TenantBranding> TenantBrandings => Set<TenantBranding>();
    public DbSet<ClaimsMappingRule> ClaimsMappingRules => Set<ClaimsMappingRule>();
    public DbSet<TokenConfiguration> TokenConfigurations => Set<TokenConfiguration>();
    public DbSet<IpAllowlistEntry> IpAllowlistEntries => Set<IpAllowlistEntry>();
    public DbSet<GeoRestriction> GeoRestrictions => Set<GeoRestriction>();
    public DbSet<GeoFence> GeoFences => Set<GeoFence>();
    public DbSet<BlockedIpLog> BlockedIpLogs => Set<BlockedIpLog>();
    public DbSet<LoginRiskScore> LoginRiskScores => Set<LoginRiskScore>();
    public DbSet<RiskThreshold> RiskThresholds => Set<RiskThreshold>();
    public DbSet<TrustedDevice> TrustedDevices => Set<TrustedDevice>();
    public DbSet<PrivilegedSession> PrivilegedSessions => Set<PrivilegedSession>();
    public DbSet<PamPolicy> PamPolicies => Set<PamPolicy>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<SecurityAlert> SecurityAlerts => Set<SecurityAlert>();
    public DbSet<SiemIntegration> SiemIntegrations => Set<SiemIntegration>();
    public DbSet<BulkOperation> BulkOperations => Set<BulkOperation>();
    public DbSet<SecretEntry> SecretEntries => Set<SecretEntry>();
    public DbSet<SecretVersion> SecretVersions => Set<SecretVersion>();
    public DbSet<Visitor> Visitors => Set<Visitor>();
    public DbSet<VisitorAccessGrant> VisitorAccessGrants => Set<VisitorAccessGrant>();
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<TokenExchange> TokenExchanges => Set<TokenExchange>();
    public DbSet<RegionConfig> RegionConfigs => Set<RegionConfig>();
    public DbSet<RegionSyncEvent> RegionSyncEvents => Set<RegionSyncEvent>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<SodConstraint> SodConstraints => Set<SodConstraint>();
    public DbSet<SodViolation> SodViolations => Set<SodViolation>();

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
            entity.Property(e => e.AvatarUrl).HasMaxLength(500);

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

            entity.HasMany(e => e.RecoveryCodes)
                .WithOne(e => e.User)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // RecoveryCode configuration
        modelBuilder.Entity<RecoveryCode>(entity =>
        {
            entity.ToTable("RecoveryCodes");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.IsUsed });
            entity.Property(e => e.CodeHash).IsRequired().HasMaxLength(255);
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

        // EmergencyOverride configuration
        modelBuilder.Entity<EmergencyOverride>(entity =>
        {
            entity.ToTable("EmergencyOverrides");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Justification).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.AccessedResources).HasMaxLength(2000);
            entity.Property(e => e.ActionsPerformed).HasMaxLength(2000);
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.DeviceId).HasMaxLength(200);
            entity.Property(e => e.IncidentTicketId).HasMaxLength(100);
            entity.Property(e => e.ReviewComments).HasMaxLength(2000);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.Status, e.ActivatedAt });
            entity.HasIndex(e => new { e.Status, e.ExpiresAt });
            entity.HasIndex(e => new { e.UserId, e.Status, e.ActivatedAt });
            entity.HasIndex(e => e.ApprovalStatus);
            entity.HasIndex(e => e.Severity);
            entity.HasIndex(e => e.SecurityNotified);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Credential configuration (WebAuthn/Passkeys)
        modelBuilder.Entity<Credential>(entity =>
        {
            entity.ToTable("Credentials");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CredentialId).IsRequired();
            entity.Property(e => e.PublicKey).IsRequired();
            entity.Property(e => e.CredType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.UserAgent).HasMaxLength(500);
            entity.Property(e => e.DeviceType).HasMaxLength(50);
            entity.Property(e => e.AttestationFormat).HasMaxLength(50);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CredentialId).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Device configuration (IoT)
        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("Devices");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceId).IsUnique();
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.DeviceType);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => new { e.DeviceType, e.IsActive });
            entity.HasIndex(e => e.ResourcePath);

            entity.Property(e => e.DeviceId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.DeviceType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.AuthenticationMethod).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ResourcePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Permissions).HasColumnType("jsonb");
            entity.Property(e => e.SharedSecretHash).HasMaxLength(255);
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
            entity.Property(e => e.Tags).HasColumnType("jsonb");
            entity.Property(e => e.LastIpAddress).HasMaxLength(50);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ProvisionedByUser)
                .WithMany()
                .HasForeignKey(e => e.ProvisionedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Certificates)
                .WithOne(e => e.Device)
                .HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DeviceCertificate configuration (IoT)
        modelBuilder.Entity<DeviceCertificate>(entity =>
        {
            entity.ToTable("DeviceCertificates");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Thumbprint).IsUnique();
            entity.HasIndex(e => e.SerialNumber);
            entity.HasIndex(e => new { e.DeviceId, e.Status });
            entity.HasIndex(e => e.NotAfter);

            entity.Property(e => e.SerialNumber).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Thumbprint).IsRequired().HasMaxLength(255);
            entity.Property(e => e.SubjectName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.IssuerName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CertificatePem).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.RevocationReason).HasMaxLength(500);
        });

        // Group configuration
        modelBuilder.Entity<Group>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.Name });
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.ParentGroupId);
            entity.HasIndex(e => new { e.TenantId, e.GroupType, e.IsActive });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.GroupType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Metadata).HasColumnType("jsonb");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ParentGroup)
                .WithMany(e => e.ChildGroups)
                .HasForeignKey(e => e.ParentGroupId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Members)
                .WithOne(e => e.Group)
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.GroupRoles)
                .WithOne(e => e.Group)
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GroupMembership configuration
        modelBuilder.Entity<GroupMembership>(entity =>
        {
            entity.ToTable("GroupMemberships");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.GroupId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.IsActive });
            entity.HasIndex(e => new { e.GroupId, e.IsActive });

            entity.Property(e => e.Role).IsRequired().HasMaxLength(50);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GroupRole configuration
        modelBuilder.Entity<GroupRole>(entity =>
        {
            entity.ToTable("GroupRoles");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.GroupId, e.RoleId }).IsUnique();
            entity.HasIndex(e => e.GroupId);
            entity.HasIndex(e => e.RoleId);
            entity.HasIndex(e => e.TenantId);

            entity.HasOne(e => e.Role)
                .WithMany()
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ApiKey configuration
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("ApiKeys");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => e.KeyPrefix);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.IsActive, e.ExpiresAt });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.KeyHash).IsRequired().HasMaxLength(128);
            entity.Property(e => e.KeyPrefix).HasMaxLength(16);
            entity.Property(e => e.Permissions).HasColumnType("jsonb");
            entity.Property(e => e.AllowedIps).HasColumnType("jsonb");
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // UserSession configuration
        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.ToTable("UserSessions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SessionToken);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.IsRevoked, e.ExpiresAt });
            entity.HasIndex(e => e.ExpiresAt);

            entity.Property(e => e.SessionToken).IsRequired().HasMaxLength(64);
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.UserAgent).HasMaxLength(500);
            entity.Property(e => e.DeviceInfo).HasMaxLength(100);
            entity.Property(e => e.Location).HasMaxLength(100);
            entity.Property(e => e.RevokedReason).HasMaxLength(50);

            // IsCurrent is not persisted - it is set at query time
            entity.Ignore(e => e.IsCurrent);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // WebhookSubscription configuration
        modelBuilder.Entity<WebhookSubscription>(entity =>
        {
            entity.ToTable("WebhookSubscriptions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => e.CreatedByUserId);

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Url).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Secret).HasMaxLength(255);
            entity.Property(e => e.Events).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.Headers).HasColumnType("jsonb");
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(50);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Deliveries)
                .WithOne(e => e.Subscription)
                .HasForeignKey(e => e.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // WebhookDelivery configuration
        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.ToTable("WebhookDeliveries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SubscriptionId);
            entity.HasIndex(e => new { e.SubscriptionId, e.CreatedAt });
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.EventType, e.CreatedAt });

            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.ResponseBody).HasMaxLength(2000);
            entity.Property(e => e.Error).HasMaxLength(500);
        });

        // TelemetryRecord configuration (time-series device telemetry)
        modelBuilder.Entity<TelemetryRecord>(entity =>
        {
            entity.ToTable("TelemetryRecords");
            entity.HasKey(e => e.Id);

            // Performance indexes for time-series queries
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => new { e.DeviceId, e.Timestamp });
            entity.HasIndex(e => new { e.MetricName, e.Timestamp });
            entity.HasIndex(e => new { e.TenantId, e.Timestamp });
            entity.HasIndex(e => new { e.DeviceId, e.MetricName, e.Timestamp });

            entity.Property(e => e.DeviceId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.MetricName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.StringValue).HasMaxLength(1000);
            entity.Property(e => e.JsonValue).HasColumnType("jsonb");
            entity.Property(e => e.Unit).HasMaxLength(50);
            entity.Property(e => e.DeviceType).HasMaxLength(100);
            entity.Property(e => e.Tags).HasColumnType("jsonb");
        });

        // IdentityProvider configuration (Social/Enterprise SSO)
        modelBuilder.Entity<IdentityProvider>(entity =>
        {
            entity.ToTable("IdentityProviders");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50)
                .HasConversion<string>();
            entity.Property(e => e.ClientId).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ClientSecret).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.MetadataUrl).HasMaxLength(2000);
            entity.Property(e => e.AttributeMapping).HasColumnType("jsonb");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.DefaultRole)
                .WithMany()
                .HasForeignKey(e => e.DefaultRoleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ExternalLogin configuration (Social/Enterprise SSO)
        modelBuilder.Entity<ExternalLogin>(entity =>
        {
            entity.ToTable("ExternalLogins");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Provider, e.ProviderUserId }).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.Provider });

            entity.Property(e => e.Provider).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ProviderUserId).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.DisplayName).HasMaxLength(255);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.IsPrimary })
                .HasFilter("\"IsPrimary\" = true")
                .IsUnique();
        });

        // MagicLinkToken configuration
        modelBuilder.Entity<MagicLinkToken>(entity =>
        {
            entity.ToTable("MagicLinkTokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });

            entity.Property(e => e.Token).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Purpose).IsRequired().HasMaxLength(50)
                .HasConversion<string>();
            entity.Property(e => e.IpAddress).HasMaxLength(50);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // OtpCode configuration
        modelBuilder.Entity<OtpCode>(entity =>
        {
            entity.ToTable("OtpCodes");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Email, e.Purpose });
            entity.HasIndex(e => new { e.PhoneNumber, e.Purpose });
            entity.HasIndex(e => e.UserId);

            entity.Property(e => e.Code).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.PhoneNumber).HasMaxLength(50);
            entity.Property(e => e.Purpose).IsRequired().HasMaxLength(50)
                .HasConversion<string>();

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ConsentRecord configuration (GDPR consent tracking)
        modelBuilder.Entity<ConsentRecord>(entity =>
        {
            entity.ToTable("ConsentRecords");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.ClientId })
                .HasFilter("\"RevokedAt\" IS NULL")
                .IsUnique();
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.GrantedAt);

            entity.Property(e => e.ClientId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Scopes).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.UserAgent).HasMaxLength(500);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DataRequest configuration (GDPR data subject requests)
        modelBuilder.Entity<DataRequest>(entity =>
        {
            entity.ToTable("DataRequests");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.Status, e.RequestedAt });
            entity.HasIndex(e => new { e.UserId, e.Type, e.Status });

            entity.Property(e => e.Type).IsRequired().HasMaxLength(50)
                .HasConversion<string>();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50)
                .HasConversion<string>();
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.DataUrl).HasColumnType("text");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DataProcessingAgreement configuration (GDPR DPA documents)
        modelBuilder.Entity<DataProcessingAgreement>(entity =>
        {
            entity.ToTable("DataProcessingAgreements");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.Version });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Version).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Content).IsRequired().HasColumnType("text");
        });

        // Invitation configuration
        modelBuilder.Entity<Invitation>(entity =>
        {
            entity.ToTable("Invitations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasIndex(e => new { e.Email, e.TenantId });
            entity.HasIndex(e => e.ExpiresAt);

            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Role)
                .WithMany()
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.InvitedByUser)
                .WithMany()
                .HasForeignKey(e => e.InvitedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // OrganizationSettings configuration
        modelBuilder.Entity<OrganizationSettings>(entity =>
        {
            entity.ToTable("OrganizationSettings");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId).IsUnique();

            entity.Property(e => e.AllowedEmailDomains).HasColumnType("jsonb");
            entity.Property(e => e.WelcomeMessage).HasMaxLength(2000);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.DefaultRole)
                .WithMany()
                .HasForeignKey(e => e.DefaultRoleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // DirectorySyncConfig configuration (LDAP/AD sync)
        modelBuilder.Entity<DirectorySyncConfig>(entity =>
        {
            entity.ToTable("DirectorySyncConfigs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => new { e.IsActive, e.LastSyncAt });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.LdapUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.BindDn).IsRequired().HasMaxLength(500);
            entity.Property(e => e.BindPassword).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.SearchBase).IsRequired().HasMaxLength(500);
            entity.Property(e => e.SearchFilter).IsRequired().HasMaxLength(500);
            entity.Property(e => e.AttributeMapping).HasColumnType("jsonb");
            entity.Property(e => e.GroupToRoleMapping).HasColumnType("jsonb");
            entity.Property(e => e.LastSyncStatus).HasMaxLength(50);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.SyncLogs)
                .WithOne(e => e.Config)
                .HasForeignKey(e => e.ConfigId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DirectorySyncLog configuration (LDAP/AD sync logs)
        modelBuilder.Entity<DirectorySyncLog>(entity =>
        {
            entity.ToTable("DirectorySyncLogs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ConfigId);
            entity.HasIndex(e => new { e.ConfigId, e.StartedAt });
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => new { e.Status, e.StartedAt });

            entity.Property(e => e.SyncType).IsRequired().HasMaxLength(20)
                .HasConversion<string>();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20)
                .HasConversion<string>();
            entity.Property(e => e.Errors).HasColumnType("jsonb");
        });

        // AccessRequest configuration (workflow-driven access requests)
        modelBuilder.Entity<AccessRequest>(entity =>
        {
            entity.ToTable("AccessRequests");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RequesterId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.RequesterId, e.Status });
            entity.HasIndex(e => new { e.Status, e.ExpiresAt });
            entity.HasIndex(e => e.WorkflowTemplateId);

            entity.Property(e => e.ResourceType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Justification).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Priority).HasConversion<int>();

            entity.HasOne(e => e.Requester)
                .WithMany()
                .HasForeignKey(e => e.RequesterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Role)
                .WithMany()
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.WorkflowTemplate)
                .WithMany()
                .HasForeignKey(e => e.WorkflowTemplateId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.ApprovalSteps)
                .WithOne(e => e.AccessRequest)
                .HasForeignKey(e => e.AccessRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ApprovalStep configuration (multi-level approval chain steps)
        modelBuilder.Entity<ApprovalStep>(entity =>
        {
            entity.ToTable("ApprovalSteps");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AccessRequestId);
            entity.HasIndex(e => new { e.AccessRequestId, e.StepOrder });
            entity.HasIndex(e => new { e.ApproverId, e.Status });
            entity.HasIndex(e => new { e.ApproverRoleId, e.Status });

            entity.Property(e => e.Comment).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();

            entity.HasOne(e => e.Approver)
                .WithMany()
                .HasForeignKey(e => e.ApproverId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ApproverRole)
                .WithMany()
                .HasForeignKey(e => e.ApproverRoleId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.DecidedByUser)
                .WithMany()
                .HasForeignKey(e => e.DecidedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // WorkflowTemplate configuration (approval workflow definitions)
        modelBuilder.Entity<WorkflowTemplate>(entity =>
        {
            entity.ToTable("WorkflowTemplates");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.ResourceType, e.IsActive });
            entity.HasIndex(e => new { e.TenantId, e.ResourceType, e.IsActive });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.ResourceType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Steps).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.AutoApproveRules).HasColumnType("jsonb");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ScimProvisioningLog configuration
        modelBuilder.Entity<ScimProvisioningLog>(entity =>
        {
            entity.ToTable("ScimProvisioningLogs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
            entity.HasIndex(e => new { e.TenantId, e.Operation, e.ResourceType });
            entity.HasIndex(e => e.ExternalId);

            entity.Property(e => e.Operation).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ResourceType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ExternalId).HasMaxLength(500);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Details).HasColumnType("jsonb");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ScimToken configuration
        modelBuilder.Entity<ScimToken>(entity =>
        {
            entity.ToTable("ScimTokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => new { e.IsActive, e.ExpiresAt });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(128);
            entity.Property(e => e.TokenPrefix).HasMaxLength(16);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TenantBranding configuration (per-tenant login page customization)
        modelBuilder.Entity<TenantBranding>(entity =>
        {
            entity.ToTable("TenantBrandings");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId).IsUnique();
            entity.HasIndex(e => e.CustomDomain).IsUnique()
                .HasFilter("\"CustomDomain\" IS NOT NULL");

            entity.Property(e => e.LogoUrl).HasMaxLength(2000);
            entity.Property(e => e.PrimaryColor).HasMaxLength(20);
            entity.Property(e => e.SecondaryColor).HasMaxLength(20);
            entity.Property(e => e.BackgroundUrl).HasMaxLength(2000);
            entity.Property(e => e.CustomCss).HasColumnType("text");
            entity.Property(e => e.EmailHeaderHtml).HasColumnType("text");
            entity.Property(e => e.EmailFooterHtml).HasColumnType("text");
            entity.Property(e => e.FaviconUrl).HasMaxLength(2000);
            entity.Property(e => e.LoginTitle).HasMaxLength(200);
            entity.Property(e => e.LoginSubtitle).HasMaxLength(500);
            entity.Property(e => e.CustomDomain).HasMaxLength(255);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ClaimsMappingRule configuration
        modelBuilder.Entity<ClaimsMappingRule>(entity =>
        {
            entity.ToTable("ClaimsMappingRules");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => new { e.ClientId, e.TenantId });
            entity.HasIndex(e => new { e.ClientId, e.IsActive, e.Priority });

            entity.Property(e => e.ClientId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.SourceType).HasConversion<int>();
            entity.Property(e => e.SourcePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.TargetClaim).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Transform).HasConversion<int>();
            entity.Property(e => e.TransformPattern).HasMaxLength(500);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TokenConfiguration configuration
        modelBuilder.Entity<TokenConfiguration>(entity =>
        {
            entity.ToTable("TokenConfigurations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => new { e.ClientId, e.TenantId }).IsUnique();

            entity.Property(e => e.ClientId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.CustomNamespace).HasMaxLength(500);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // IpAllowlistEntry configuration (network security)
        modelBuilder.Entity<IpAllowlistEntry>(entity =>
        {
            entity.ToTable("IpAllowlistEntries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });

            entity.Property(e => e.Cidr).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GeoRestriction configuration (country-level access control)
        modelBuilder.Entity<GeoRestriction>(entity =>
        {
            entity.ToTable("GeoRestrictions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });

            entity.Property(e => e.AllowedCountries).HasColumnType("jsonb");
            entity.Property(e => e.BlockedCountries).HasColumnType("jsonb");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GeoFence configuration (radius-based location access control)
        modelBuilder.Entity<GeoFence>(entity =>
        {
            entity.ToTable("GeoFences");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(1000);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BlockedIpLog configuration (audit trail for blocked access attempts)
        modelBuilder.Entity<BlockedIpLog>(entity =>
        {
            entity.ToTable("BlockedIpLogs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.BlockedAt);
            entity.HasIndex(e => new { e.TenantId, e.BlockedAt });
            entity.HasIndex(e => e.IpAddress);

            entity.Property(e => e.IpAddress).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Country).HasMaxLength(10);
            entity.Property(e => e.City).HasMaxLength(200);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // LoginRiskScore configuration (risk-based adaptive authentication)
        modelBuilder.Entity<LoginRiskScore>(entity =>
        {
            entity.ToTable("LoginRiskScores");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
            entity.HasIndex(e => e.IpAddress);
            entity.HasIndex(e => new { e.Action, e.CreatedAt });

            entity.Property(e => e.IpAddress).IsRequired().HasMaxLength(50);
            entity.Property(e => e.UserAgent).HasMaxLength(500);
            entity.Property(e => e.GeoLocation).HasMaxLength(200);
            entity.Property(e => e.RiskFactors).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.Action).HasConversion<int>();

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // RiskThreshold configuration (per-tenant risk policy)
        modelBuilder.Entity<RiskThreshold>(entity =>
        {
            entity.ToTable("RiskThresholds");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TrustedDevice configuration (device trust for risk reduction)
        modelBuilder.Entity<TrustedDevice>(entity =>
        {
            entity.ToTable("TrustedDevices");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.DeviceFingerprint });
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => new { e.UserId, e.ExpiresAt });

            entity.Property(e => e.DeviceFingerprint).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PrivilegedSession configuration (PAM)
        modelBuilder.Entity<PrivilegedSession>(entity =>
        {
            entity.ToTable("PrivilegedSessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Justification).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.AuditCorrelationId).HasMaxLength(100);
            entity.Property(e => e.BreakGlassApprovers).HasColumnType("jsonb");

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.RoleId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.Status, e.ExpiresAt });
            entity.HasIndex(e => new { e.UserId, e.RoleId, e.TenantId, e.Status });
            entity.HasIndex(e => e.AuditCorrelationId);
            entity.HasIndex(e => new { e.IsBreakGlass, e.Status });

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

            entity.HasOne(e => e.PamPolicy)
                .WithMany()
                .HasForeignKey(e => e.PamPolicyId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // PamPolicy configuration (PAM)
        modelBuilder.Entity<PamPolicy>(entity =>
        {
            entity.ToTable("PamPolicies");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.RoleId, e.TenantId }).IsUnique();
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.IsActive, e.TenantId });

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Role)
                .WithMany()
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ApproverRole)
                .WithMany()
                .HasForeignKey(e => e.ApproverRoleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // BulkOperation configuration
        modelBuilder.Entity<BulkOperation>(entity =>
        {
            entity.ToTable("BulkOperations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => e.CreatedByUserId);

            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Format).HasConversion<int>();
            entity.Property(e => e.FileName).HasMaxLength(500);
            entity.Property(e => e.ErrorDetails).HasColumnType("jsonb");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SecretEntry configuration (Secrets Vault)
        modelBuilder.Entity<SecretEntry>(entity =>
        {
            entity.ToTable("SecretEntries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => new { e.TenantId, e.Name });
            entity.HasIndex(e => new { e.IsActive, e.NextRotationAt });
            entity.HasIndex(e => e.SecretType);

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.EncryptedValue).IsRequired();
            entity.Property(e => e.IV).IsRequired().HasMaxLength(32);
            entity.Property(e => e.RotationSchedule).HasColumnType("jsonb");
            entity.Property(e => e.SecretType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Tags).HasColumnType("jsonb");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SecretVersion configuration (Secrets Vault version history)
        modelBuilder.Entity<SecretVersion>(entity =>
        {
            entity.ToTable("SecretVersions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SecretEntryId);
            entity.HasIndex(e => new { e.SecretEntryId, e.Version });
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.SecretEntryId, e.CreatedAt });

            entity.Property(e => e.EncryptedValue).IsRequired();
            entity.Property(e => e.IV).IsRequired().HasMaxLength(32);
            entity.Property(e => e.RotationReason).IsRequired().HasMaxLength(50);

            entity.HasOne(e => e.SecretEntry)
                .WithMany()
                .HasForeignKey(e => e.SecretEntryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AlertRule configuration (security event alerting)
        modelBuilder.Entity<AlertRule>(entity =>
        {
            entity.ToTable("AlertRules");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.IsActive, e.Severity });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Condition).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.Severity).HasConversion<int>();
            entity.Property(e => e.Channels).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.AutoResponseAction).HasMaxLength(50);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Alerts)
                .WithOne(e => e.Rule)
                .HasForeignKey(e => e.RuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SecurityAlert configuration (fired alert instances)
        modelBuilder.Entity<SecurityAlert>(entity =>
        {
            entity.ToTable("SecurityAlerts");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.RuleId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.Severity, e.AcknowledgedAt });
            entity.HasIndex(e => new { e.TenantId, e.AcknowledgedAt });

            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Severity).HasConversion<int>();
            entity.Property(e => e.Details).HasColumnType("jsonb");
            entity.Property(e => e.AutoResponseAction).HasMaxLength(50);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.AcknowledgedByUser)
                .WithMany()
                .HasForeignKey(e => e.AcknowledgedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // SiemIntegration configuration (SIEM platform connections)
        modelBuilder.Entity<SiemIntegration>(entity =>
        {
            entity.ToTable("SiemIntegrations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.IsActive, e.Type });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.EndpointUrl).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.AuthConfig).HasColumnType("jsonb");
            entity.Property(e => e.Format).IsRequired().HasMaxLength(20);
            entity.Property(e => e.EventFilter).HasColumnType("jsonb");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Visitor configuration (visitor pre-registration and management)
        modelBuilder.Entity<Visitor>(entity =>
        {
            entity.ToTable("Visitors");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.HostUserId);
            entity.HasIndex(e => new { e.TenantId, e.VisitDate });
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasIndex(e => e.QrToken).IsUnique();
            entity.HasIndex(e => e.Email);

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Company).HasMaxLength(255);
            entity.Property(e => e.QrToken).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Purpose).HasMaxLength(1000);
            entity.Property(e => e.Status).HasConversion<int>();

            entity.HasOne(e => e.HostUser)
                .WithMany()
                .HasForeignKey(e => e.HostUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.AccessGrants)
                .WithOne(e => e.Visitor)
                .HasForeignKey(e => e.VisitorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // VisitorAccessGrant configuration (temporary resource access for visitors)
        modelBuilder.Entity<VisitorAccessGrant>(entity =>
        {
            entity.ToTable("VisitorAccessGrants");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.VisitorId);
            entity.HasIndex(e => new { e.VisitorId, e.ValidFrom, e.ValidUntil });

            entity.Property(e => e.Resources).IsRequired().HasColumnType("jsonb");
        });

        // ServiceAccount configuration (API gateway / service mesh authentication)
        modelBuilder.Entity<ServiceAccount>(entity =>
        {
            entity.ToTable("ServiceAccounts");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClientId).IsUnique();
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.IsActive, e.Type });
            entity.HasIndex(e => e.CertificateThumbprint)
                .HasFilter("\"CertificateThumbprint\" IS NOT NULL");

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ClientId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.ClientSecretHash).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Permissions).HasColumnType("jsonb");
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.CertificateThumbprint).HasMaxLength(128);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TokenExchange configuration (RFC 8693 token exchange audit trail)
        modelBuilder.Entity<TokenExchange>(entity =>
        {
            entity.ToTable("TokenExchanges");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SubjectId);
            entity.HasIndex(e => e.TargetService);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.SubjectId, e.TargetService, e.CreatedAt });
            entity.HasIndex(e => e.ExpiresAt);

            entity.Property(e => e.OriginalTokenHash).IsRequired().HasMaxLength(128);
            entity.Property(e => e.ExchangedTokenHash).IsRequired().HasMaxLength(128);
            entity.Property(e => e.TargetService).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Scopes).HasMaxLength(1000);

            entity.HasOne(e => e.ServiceAccount)
                .WithMany()
                .HasForeignKey(e => e.ServiceAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // RegionConfig configuration (multi-region HA)
        modelBuilder.Entity<RegionConfig>(entity =>
        {
            entity.ToTable("RegionConfigs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.IsPrimary);
            entity.HasIndex(e => new { e.Status, e.Priority });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Endpoint).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Status).HasConversion<int>();
        });

        // RegionSyncEvent configuration (cross-region sync tracking)
        modelBuilder.Entity<RegionSyncEvent>(entity =>
        {
            entity.ToTable("RegionSyncEvents");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.SyncStatus);
            entity.HasIndex(e => new { e.SourceRegion, e.TargetRegion });
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasIndex(e => new { e.SyncStatus, e.CreatedAt });

            entity.Property(e => e.SourceRegion).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TargetRegion).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EntityId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.SyncStatus).HasConversion<int>();
            entity.Property(e => e.ConflictResolution).HasConversion<int>();
            entity.Property(e => e.Details).HasColumnType("jsonb");
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
        });

        // Delegation configuration (permission delegation between users)
        modelBuilder.Entity<Delegation>(entity =>
        {
            entity.ToTable("Delegations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DelegatorUserId);
            entity.HasIndex(e => e.DelegateUserId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.DelegateUserId, e.IsActive, e.ValidFrom, e.ValidUntil });
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasIndex(e => new { e.IsActive, e.ValidUntil });

            entity.Property(e => e.Permissions).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();

            entity.HasOne(e => e.DelegatorUser)
                .WithMany()
                .HasForeignKey(e => e.DelegatorUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.DelegateUser)
                .WithMany()
                .HasForeignKey(e => e.DelegateUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SodConstraint configuration (segregation of duties rules)
        modelBuilder.Entity<SodConstraint>(entity =>
        {
            entity.ToTable("SodConstraints");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => new { e.ConflictingRoleA, e.ConflictingRoleB, e.TenantId });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Severity).HasConversion<int>();

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.RoleA)
                .WithMany()
                .HasForeignKey(e => e.ConflictingRoleA)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.RoleB)
                .WithMany()
                .HasForeignKey(e => e.ConflictingRoleB)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // SodViolation configuration (detected SoD violations)
        modelBuilder.Entity<SodViolation>(entity =>
        {
            entity.ToTable("SodViolations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ConstraintId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.ConstraintId, e.ResolvedAt });
            entity.HasIndex(e => e.DetectedAt);
            entity.HasIndex(e => new { e.ResolvedAt, e.DetectedAt });

            entity.Property(e => e.Resolution).HasMaxLength(2000);

            entity.HasOne(e => e.Constraint)
                .WithMany()
                .HasForeignKey(e => e.ConstraintId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
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
