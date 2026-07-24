using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationsAndOrganizationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "Users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Roles",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Condition = table.Column<string>(type: "jsonb", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Channels = table.Column<string>(type: "jsonb", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: false),
                    AutoResponseAction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlockedIpLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Country = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    City = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedIpLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlockedIpLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BulkOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    ProcessedRows = table.Column<int>(type: "integer", nullable: false),
                    SuccessRows = table.Column<int>(type: "integer", nullable: false),
                    ErrorRows = table.Column<int>(type: "integer", nullable: false),
                    ErrorDetails = table.Column<string>(type: "jsonb", nullable: true),
                    FileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResultUrl = table.Column<string>(type: "text", nullable: true),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BulkOperations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BulkOperations_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClaimsMappingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TargetClaim = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Transform = table.Column<int>(type: "integer", nullable: false),
                    TransformPattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimsMappingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimsMappingRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConsentRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Scopes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentRecords_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataProcessingAgreements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    RequiresExplicitConsent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProcessingAgreements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DataUrl = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Delegations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegatorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegateUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<string>(type: "jsonb", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delegations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Delegations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Delegations_Users_DelegateUserId",
                        column: x => x.DelegateUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delegations_Users_DelegatorUserId",
                        column: x => x.DelegatorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectorySyncConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LdapUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BindDn = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BindPassword = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SearchBase = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SearchFilter = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SyncInterval = table.Column<int>(type: "integer", nullable: false),
                    AttributeMapping = table.Column<string>(type: "jsonb", nullable: false),
                    GroupToRoleMapping = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorySyncConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorySyncConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalLogins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderUserId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalLogins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalLogins_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeoFences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    RadiusMeters = table.Column<double>(type: "double precision", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeoFences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GeoFences_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeoRestrictions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedCountries = table.Column<string>(type: "jsonb", nullable: true),
                    BlockedCountries = table.Column<string>(type: "jsonb", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeoRestrictions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GeoRestrictions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ClientSecret = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    MetadataUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AttributeMapping = table.Column<string>(type: "jsonb", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    AutoCreateUsers = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityProviders_Roles_DefaultRoleId",
                        column: x => x.DefaultRoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IdentityProviders_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invitations_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Invitations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Invitations_Users_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IpAllowlistEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cidr = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpAllowlistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IpAllowlistEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoginRiskScores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    GeoLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RiskScore = table.Column<int>(type: "integer", nullable: false),
                    RiskFactors = table.Column<string>(type: "jsonb", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginRiskScores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoginRiskScores_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MagicLinkTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MagicLinkTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MagicLinkTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedEmailDomains = table.Column<string>(type: "jsonb", nullable: false),
                    RequireMfa = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    MaxMembers = table.Column<int>(type: "integer", nullable: false),
                    WelcomeMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationSettings_Roles_DefaultRoleId",
                        column: x => x.DefaultRoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrganizationSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OtpCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Code = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtpCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OtpCodes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PamPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaxDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    RequireJustification = table.Column<bool>(type: "boolean", nullable: false),
                    RequireApproval = table.Column<bool>(type: "boolean", nullable: false),
                    ApproverRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    BreakGlassEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    BreakGlassApproversRequired = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PamPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PamPolicies_Roles_ApproverRoleId",
                        column: x => x.ApproverRoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PamPolicies_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PamPolicies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RegionConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastHealthCheck = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegionConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegionSyncEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRegion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetRegion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SyncStatus = table.Column<int>(type: "integer", nullable: false),
                    ConflictResolution = table.Column<int>(type: "integer", nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegionSyncEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskThresholds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    LowThreshold = table.Column<int>(type: "integer", nullable: false),
                    MediumThreshold = table.Column<int>(type: "integer", nullable: false),
                    HighThreshold = table.Column<int>(type: "integer", nullable: false),
                    BlockThreshold = table.Column<int>(type: "integer", nullable: false),
                    RequireMfaAbove = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskThresholds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskThresholds_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimProvisioningLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimProvisioningLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScimProvisioningLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TokenPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScimTokens_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecretEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EncryptedValue = table.Column<string>(type: "text", nullable: false),
                    IV = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RotationSchedule = table.Column<string>(type: "jsonb", nullable: true),
                    LastRotatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRotationAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SecretType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Tags = table.Column<string>(type: "jsonb", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecretEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClientSecretHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Permissions = table.Column<string>(type: "jsonb", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAuthenticatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceAccounts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SiemIntegrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    EndpointUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AuthConfig = table.Column<string>(type: "jsonb", nullable: true),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EventFilter = table.Column<string>(type: "jsonb", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiemIntegrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiemIntegrations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SodConstraints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConflictingRoleA = table.Column<Guid>(type: "uuid", nullable: false),
                    ConflictingRoleB = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SodConstraints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SodConstraints_Roles_ConflictingRoleA",
                        column: x => x.ConflictingRoleA,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SodConstraints_Roles_ConflictingRoleB",
                        column: x => x.ConflictingRoleB,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SodConstraints_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantBrandings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LogoUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PrimaryColor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SecondaryColor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BackgroundUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CustomCss = table.Column<string>(type: "text", nullable: true),
                    EmailHeaderHtml = table.Column<string>(type: "text", nullable: true),
                    EmailFooterHtml = table.Column<string>(type: "text", nullable: true),
                    FaviconUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LoginTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LoginSubtitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WhiteLabelEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CustomDomain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBrandings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantBrandings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TokenConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccessTokenLifetimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    RefreshTokenLifetimeDays = table.Column<int>(type: "integer", nullable: false),
                    IncludeRoles = table.Column<bool>(type: "boolean", nullable: false),
                    IncludePermissions = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeGroups = table.Column<bool>(type: "boolean", nullable: false),
                    CustomNamespace = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TokenConfigurations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrustedDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TrustScore = table.Column<int>(type: "integer", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrustedDevices_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Visitors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Company = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    HostUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CheckInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    QrToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Visitors_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Visitors_Users_HostUserId",
                        column: x => x.HostUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ResourceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Steps = table.Column<string>(type: "jsonb", nullable: false),
                    AutoExpireHours = table.Column<int>(type: "integer", nullable: true),
                    AutoApproveRules = table.Column<string>(type: "jsonb", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AutoResponseAction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityAlerts_AlertRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SecurityAlerts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SecurityAlerts_Users_AcknowledgedByUserId",
                        column: x => x.AcknowledgedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DirectorySyncLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UsersCreated = table.Column<int>(type: "integer", nullable: false),
                    UsersUpdated = table.Column<int>(type: "integer", nullable: false),
                    UsersDisabled = table.Column<int>(type: "integer", nullable: false),
                    GroupsSynced = table.Column<int>(type: "integer", nullable: false),
                    Errors = table.Column<string>(type: "jsonb", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorySyncLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorySyncLogs_DirectorySyncConfigs_ConfigId",
                        column: x => x.ConfigId,
                        principalTable: "DirectorySyncConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrivilegedSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CheckedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsBreakGlass = table.Column<bool>(type: "boolean", nullable: false),
                    BreakGlassApprovers = table.Column<string>(type: "jsonb", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AuditCorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PamPolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivilegedSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrivilegedSessions_PamPolicies_PamPolicyId",
                        column: x => x.PamPolicyId,
                        principalTable: "PamPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PrivilegedSessions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrivilegedSessions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrivilegedSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecretVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EncryptedValue = table.Column<string>(type: "text", nullable: false),
                    IV = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    RotationReason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RotatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GracePeriodEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecretVersions_SecretEntries_SecretEntryId",
                        column: x => x.SecretEntryId,
                        principalTable: "SecretEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TokenExchanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExchangedTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetService = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Scopes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    ServiceAccountId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenExchanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TokenExchanges_ServiceAccounts_ServiceAccountId",
                        column: x => x.ServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SodViolations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConstraintId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleA = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleB = table.Column<Guid>(type: "uuid", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Resolution = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SodViolations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SodViolations_SodConstraints_ConstraintId",
                        column: x => x.ConstraintId,
                        principalTable: "SodConstraints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SodViolations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VisitorAccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Resources = table.Column<string>(type: "jsonb", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitorAccessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisitorAccessGrants_Visitors_VisitorId",
                        column: x => x.VisitorId,
                        principalTable: "Visitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    WorkflowTemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRequests_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AccessRequests_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AccessRequests_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessRequests_WorkflowTemplates_WorkflowTemplateId",
                        column: x => x.WorkflowTemplateId,
                        principalTable: "WorkflowTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccessRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepOrder = table.Column<int>(type: "integer", nullable: false),
                    ApproverId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApproverRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    QuorumCount = table.Column<int>(type: "integer", nullable: false),
                    ApprovalsReceived = table.Column<int>(type: "integer", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalSteps_AccessRequests_AccessRequestId",
                        column: x => x.AccessRequestId,
                        principalTable: "AccessRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApprovalSteps_Roles_ApproverRoleId",
                        column: x => x.ApproverRoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ApprovalSteps_Users_ApproverId",
                        column: x => x.ApproverId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ApprovalSteps_Users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "Category",
                value: null);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000002"),
                column: "Category",
                value: null);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000003"),
                column: "Category",
                value: null);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000004"),
                column: "Category",
                value: null);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000005"),
                column: "Category",
                value: null);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000006"),
                column: "Category",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_RequesterId",
                table: "AccessRequests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_RequesterId_Status",
                table: "AccessRequests",
                columns: new[] { "RequesterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_RoleId",
                table: "AccessRequests",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_Status_CreatedAt",
                table: "AccessRequests",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_Status_ExpiresAt",
                table: "AccessRequests",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_TenantId",
                table: "AccessRequests",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_WorkflowTemplateId",
                table: "AccessRequests",
                column: "WorkflowTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_IsActive_Severity",
                table: "AlertRules",
                columns: new[] { "IsActive", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_TenantId",
                table: "AlertRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalSteps_AccessRequestId",
                table: "ApprovalSteps",
                column: "AccessRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalSteps_AccessRequestId_StepOrder",
                table: "ApprovalSteps",
                columns: new[] { "AccessRequestId", "StepOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalSteps_ApproverId_Status",
                table: "ApprovalSteps",
                columns: new[] { "ApproverId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalSteps_ApproverRoleId_Status",
                table: "ApprovalSteps",
                columns: new[] { "ApproverRoleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalSteps_DecidedByUserId",
                table: "ApprovalSteps",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedIpLogs_BlockedAt",
                table: "BlockedIpLogs",
                column: "BlockedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedIpLogs_IpAddress",
                table: "BlockedIpLogs",
                column: "IpAddress");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedIpLogs_TenantId",
                table: "BlockedIpLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedIpLogs_TenantId_BlockedAt",
                table: "BlockedIpLogs",
                columns: new[] { "TenantId", "BlockedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperations_CreatedByUserId",
                table: "BulkOperations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperations_Status_CreatedAt",
                table: "BulkOperations",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperations_TenantId",
                table: "BulkOperations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperations_TenantId_CreatedAt",
                table: "BulkOperations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimsMappingRules_ClientId",
                table: "ClaimsMappingRules",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimsMappingRules_ClientId_IsActive_Priority",
                table: "ClaimsMappingRules",
                columns: new[] { "ClientId", "IsActive", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimsMappingRules_ClientId_TenantId",
                table: "ClaimsMappingRules",
                columns: new[] { "ClientId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimsMappingRules_TenantId",
                table: "ClaimsMappingRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_ClientId",
                table: "ConsentRecords",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_GrantedAt",
                table: "ConsentRecords",
                column: "GrantedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_UserId",
                table: "ConsentRecords",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_UserId_ClientId",
                table: "ConsentRecords",
                columns: new[] { "UserId", "ClientId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DataProcessingAgreements_TenantId",
                table: "DataProcessingAgreements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DataProcessingAgreements_TenantId_Version",
                table: "DataProcessingAgreements",
                columns: new[] { "TenantId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_DataRequests_Status",
                table: "DataRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DataRequests_Status_RequestedAt",
                table: "DataRequests",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DataRequests_UserId",
                table: "DataRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DataRequests_UserId_Type_Status",
                table: "DataRequests",
                columns: new[] { "UserId", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_DelegateUserId",
                table: "Delegations",
                column: "DelegateUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_DelegateUserId_IsActive_ValidFrom_ValidUntil",
                table: "Delegations",
                columns: new[] { "DelegateUserId", "IsActive", "ValidFrom", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_DelegatorUserId",
                table: "Delegations",
                column: "DelegatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_IsActive_ValidUntil",
                table: "Delegations",
                columns: new[] { "IsActive", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_TenantId",
                table: "Delegations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_TenantId_Status",
                table: "Delegations",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncConfigs_IsActive_LastSyncAt",
                table: "DirectorySyncConfigs",
                columns: new[] { "IsActive", "LastSyncAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncConfigs_TenantId",
                table: "DirectorySyncConfigs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncConfigs_TenantId_IsActive",
                table: "DirectorySyncConfigs",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncLogs_ConfigId",
                table: "DirectorySyncLogs",
                column: "ConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncLogs_ConfigId_StartedAt",
                table: "DirectorySyncLogs",
                columns: new[] { "ConfigId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncLogs_StartedAt",
                table: "DirectorySyncLogs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncLogs_Status_StartedAt",
                table: "DirectorySyncLogs",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_TenantId_Key",
                table: "EmailTemplates",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLogins_Provider_ProviderUserId",
                table: "ExternalLogins",
                columns: new[] { "Provider", "ProviderUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLogins_UserId",
                table: "ExternalLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLogins_UserId_IsPrimary",
                table: "ExternalLogins",
                columns: new[] { "UserId", "IsPrimary" },
                unique: true,
                filter: "\"IsPrimary\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLogins_UserId_Provider",
                table: "ExternalLogins",
                columns: new[] { "UserId", "Provider" });

            migrationBuilder.CreateIndex(
                name: "IX_GeoFences_TenantId",
                table: "GeoFences",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GeoFences_TenantId_IsActive",
                table: "GeoFences",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_GeoRestrictions_TenantId",
                table: "GeoRestrictions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GeoRestrictions_TenantId_IsActive",
                table: "GeoRestrictions",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_DefaultRoleId",
                table: "IdentityProviders",
                column: "DefaultRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_Name",
                table: "IdentityProviders",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_TenantId",
                table: "IdentityProviders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_TenantId_IsActive",
                table: "IdentityProviders",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_Email_TenantId",
                table: "Invitations",
                columns: new[] { "Email", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_ExpiresAt",
                table: "Invitations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_InvitedByUserId",
                table: "Invitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_RoleId",
                table: "Invitations",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TenantId_Status",
                table: "Invitations",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_Token",
                table: "Invitations",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IpAllowlistEntries_TenantId",
                table: "IpAllowlistEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IpAllowlistEntries_TenantId_IsActive",
                table: "IpAllowlistEntries",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_LoginRiskScores_Action_CreatedAt",
                table: "LoginRiskScores",
                columns: new[] { "Action", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LoginRiskScores_CreatedAt",
                table: "LoginRiskScores",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LoginRiskScores_IpAddress",
                table: "LoginRiskScores",
                column: "IpAddress");

            migrationBuilder.CreateIndex(
                name: "IX_LoginRiskScores_UserId",
                table: "LoginRiskScores",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_LoginRiskScores_UserId_CreatedAt",
                table: "LoginRiskScores",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkTokens_Token",
                table: "MagicLinkTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkTokens_UserId",
                table: "MagicLinkTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkTokens_UserId_CreatedAt",
                table: "MagicLinkTokens",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationSettings_DefaultRoleId",
                table: "OrganizationSettings",
                column: "DefaultRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationSettings_TenantId",
                table: "OrganizationSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtpCodes_Email_Purpose",
                table: "OtpCodes",
                columns: new[] { "Email", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_OtpCodes_PhoneNumber_Purpose",
                table: "OtpCodes",
                columns: new[] { "PhoneNumber", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_OtpCodes_UserId",
                table: "OtpCodes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PamPolicies_ApproverRoleId",
                table: "PamPolicies",
                column: "ApproverRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_PamPolicies_IsActive_TenantId",
                table: "PamPolicies",
                columns: new[] { "IsActive", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PamPolicies_RoleId_TenantId",
                table: "PamPolicies",
                columns: new[] { "RoleId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PamPolicies_TenantId",
                table: "PamPolicies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PrivilegedSessions_AuditCorrelationId",
                table: "PrivilegedSessions",
                column: "AuditCorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_PrivilegedSessions_IsBreakGlass_Status",
                table: "PrivilegedSessions",
                columns: new[] { "IsBreakGlass", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PrivilegedSessions_PamPolicyId",
                table: "PrivilegedSessions",
                column: "PamPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_PrivilegedSessions_RoleId",
                table: "PrivilegedSessions",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_PrivilegedSessions_Status_ExpiresAt",
                table: "PrivilegedSessions",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PrivilegedSessions_TenantId",
                table: "PrivilegedSessions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PrivilegedSessions_UserId",
                table: "PrivilegedSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PrivilegedSessions_UserId_RoleId_TenantId_Status",
                table: "PrivilegedSessions",
                columns: new[] { "UserId", "RoleId", "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionConfigs_IsPrimary",
                table: "RegionConfigs",
                column: "IsPrimary");

            migrationBuilder.CreateIndex(
                name: "IX_RegionConfigs_Name",
                table: "RegionConfigs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegionConfigs_Status_Priority",
                table: "RegionConfigs",
                columns: new[] { "Status", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionSyncEvents_CreatedAt",
                table: "RegionSyncEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RegionSyncEvents_EntityType_EntityId",
                table: "RegionSyncEvents",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionSyncEvents_SourceRegion_TargetRegion",
                table: "RegionSyncEvents",
                columns: new[] { "SourceRegion", "TargetRegion" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionSyncEvents_SyncStatus",
                table: "RegionSyncEvents",
                column: "SyncStatus");

            migrationBuilder.CreateIndex(
                name: "IX_RegionSyncEvents_SyncStatus_CreatedAt",
                table: "RegionSyncEvents",
                columns: new[] { "SyncStatus", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskThresholds_TenantId",
                table: "RiskThresholds",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskThresholds_TenantId_IsActive",
                table: "RiskThresholds",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimProvisioningLogs_CreatedAt",
                table: "ScimProvisioningLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScimProvisioningLogs_ExternalId",
                table: "ScimProvisioningLogs",
                column: "ExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimProvisioningLogs_TenantId",
                table: "ScimProvisioningLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimProvisioningLogs_TenantId_CreatedAt",
                table: "ScimProvisioningLogs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimProvisioningLogs_TenantId_Operation_ResourceType",
                table: "ScimProvisioningLogs",
                columns: new[] { "TenantId", "Operation", "ResourceType" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimTokens_IsActive_ExpiresAt",
                table: "ScimTokens",
                columns: new[] { "IsActive", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimTokens_TenantId",
                table: "ScimTokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimTokens_TenantId_IsActive",
                table: "ScimTokens",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimTokens_TokenHash",
                table: "ScimTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecretEntries_IsActive_NextRotationAt",
                table: "SecretEntries",
                columns: new[] { "IsActive", "NextRotationAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecretEntries_Name",
                table: "SecretEntries",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_SecretEntries_SecretType",
                table: "SecretEntries",
                column: "SecretType");

            migrationBuilder.CreateIndex(
                name: "IX_SecretEntries_TenantId",
                table: "SecretEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SecretEntries_TenantId_Name",
                table: "SecretEntries",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_SecretVersions_CreatedAt",
                table: "SecretVersions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SecretVersions_SecretEntryId",
                table: "SecretVersions",
                column: "SecretEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_SecretVersions_SecretEntryId_CreatedAt",
                table: "SecretVersions",
                columns: new[] { "SecretEntryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecretVersions_SecretEntryId_Version",
                table: "SecretVersions",
                columns: new[] { "SecretEntryId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_AcknowledgedByUserId",
                table: "SecurityAlerts",
                column: "AcknowledgedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_CreatedAt",
                table: "SecurityAlerts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_RuleId",
                table: "SecurityAlerts",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_Severity_AcknowledgedAt",
                table: "SecurityAlerts",
                columns: new[] { "Severity", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_TenantId",
                table: "SecurityAlerts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_TenantId_AcknowledgedAt",
                table: "SecurityAlerts",
                columns: new[] { "TenantId", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_CertificateThumbprint",
                table: "ServiceAccounts",
                column: "CertificateThumbprint",
                filter: "\"CertificateThumbprint\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_ClientId",
                table: "ServiceAccounts",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_IsActive_Type",
                table: "ServiceAccounts",
                columns: new[] { "IsActive", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_TenantId",
                table: "ServiceAccounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SiemIntegrations_IsActive_Type",
                table: "SiemIntegrations",
                columns: new[] { "IsActive", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_SiemIntegrations_TenantId",
                table: "SiemIntegrations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SodConstraints_ConflictingRoleA_ConflictingRoleB_TenantId",
                table: "SodConstraints",
                columns: new[] { "ConflictingRoleA", "ConflictingRoleB", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SodConstraints_ConflictingRoleB",
                table: "SodConstraints",
                column: "ConflictingRoleB");

            migrationBuilder.CreateIndex(
                name: "IX_SodConstraints_TenantId",
                table: "SodConstraints",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SodConstraints_TenantId_IsActive",
                table: "SodConstraints",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_SodViolations_ConstraintId",
                table: "SodViolations",
                column: "ConstraintId");

            migrationBuilder.CreateIndex(
                name: "IX_SodViolations_DetectedAt",
                table: "SodViolations",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SodViolations_ResolvedAt_DetectedAt",
                table: "SodViolations",
                columns: new[] { "ResolvedAt", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SodViolations_UserId",
                table: "SodViolations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SodViolations_UserId_ConstraintId_ResolvedAt",
                table: "SodViolations",
                columns: new[] { "UserId", "ConstraintId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBrandings_CustomDomain",
                table: "TenantBrandings",
                column: "CustomDomain",
                unique: true,
                filter: "\"CustomDomain\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBrandings_TenantId",
                table: "TenantBrandings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TokenConfigurations_ClientId",
                table: "TokenConfigurations",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_TokenConfigurations_ClientId_TenantId",
                table: "TokenConfigurations",
                columns: new[] { "ClientId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TokenConfigurations_TenantId",
                table: "TokenConfigurations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TokenExchanges_CreatedAt",
                table: "TokenExchanges",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TokenExchanges_ExpiresAt",
                table: "TokenExchanges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_TokenExchanges_ServiceAccountId",
                table: "TokenExchanges",
                column: "ServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TokenExchanges_SubjectId",
                table: "TokenExchanges",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TokenExchanges_SubjectId_TargetService_CreatedAt",
                table: "TokenExchanges",
                columns: new[] { "SubjectId", "TargetService", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TokenExchanges_TargetService",
                table: "TokenExchanges",
                column: "TargetService");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_ExpiresAt",
                table: "TrustedDevices",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_UserId",
                table: "TrustedDevices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_UserId_DeviceFingerprint",
                table: "TrustedDevices",
                columns: new[] { "UserId", "DeviceFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_UserId_ExpiresAt",
                table: "TrustedDevices",
                columns: new[] { "UserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitorAccessGrants_VisitorId",
                table: "VisitorAccessGrants",
                column: "VisitorId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitorAccessGrants_VisitorId_ValidFrom_ValidUntil",
                table: "VisitorAccessGrants",
                columns: new[] { "VisitorId", "ValidFrom", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Visitors_Email",
                table: "Visitors",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Visitors_HostUserId",
                table: "Visitors",
                column: "HostUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Visitors_QrToken",
                table: "Visitors",
                column: "QrToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Visitors_TenantId",
                table: "Visitors",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Visitors_TenantId_Status",
                table: "Visitors",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Visitors_TenantId_VisitDate",
                table: "Visitors",
                columns: new[] { "TenantId", "VisitDate" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplates_ResourceType_IsActive",
                table: "WorkflowTemplates",
                columns: new[] { "ResourceType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplates_TenantId",
                table: "WorkflowTemplates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplates_TenantId_ResourceType_IsActive",
                table: "WorkflowTemplates",
                columns: new[] { "TenantId", "ResourceType", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalSteps");

            migrationBuilder.DropTable(
                name: "BlockedIpLogs");

            migrationBuilder.DropTable(
                name: "BulkOperations");

            migrationBuilder.DropTable(
                name: "ClaimsMappingRules");

            migrationBuilder.DropTable(
                name: "ConsentRecords");

            migrationBuilder.DropTable(
                name: "DataProcessingAgreements");

            migrationBuilder.DropTable(
                name: "DataRequests");

            migrationBuilder.DropTable(
                name: "Delegations");

            migrationBuilder.DropTable(
                name: "DirectorySyncLogs");

            migrationBuilder.DropTable(
                name: "EmailTemplates");

            migrationBuilder.DropTable(
                name: "ExternalLogins");

            migrationBuilder.DropTable(
                name: "GeoFences");

            migrationBuilder.DropTable(
                name: "GeoRestrictions");

            migrationBuilder.DropTable(
                name: "IdentityProviders");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "IpAllowlistEntries");

            migrationBuilder.DropTable(
                name: "LoginRiskScores");

            migrationBuilder.DropTable(
                name: "MagicLinkTokens");

            migrationBuilder.DropTable(
                name: "OrganizationSettings");

            migrationBuilder.DropTable(
                name: "OtpCodes");

            migrationBuilder.DropTable(
                name: "PrivilegedSessions");

            migrationBuilder.DropTable(
                name: "RegionConfigs");

            migrationBuilder.DropTable(
                name: "RegionSyncEvents");

            migrationBuilder.DropTable(
                name: "RiskThresholds");

            migrationBuilder.DropTable(
                name: "ScimProvisioningLogs");

            migrationBuilder.DropTable(
                name: "ScimTokens");

            migrationBuilder.DropTable(
                name: "SecretVersions");

            migrationBuilder.DropTable(
                name: "SecurityAlerts");

            migrationBuilder.DropTable(
                name: "SiemIntegrations");

            migrationBuilder.DropTable(
                name: "SodViolations");

            migrationBuilder.DropTable(
                name: "TenantBrandings");

            migrationBuilder.DropTable(
                name: "TokenConfigurations");

            migrationBuilder.DropTable(
                name: "TokenExchanges");

            migrationBuilder.DropTable(
                name: "TrustedDevices");

            migrationBuilder.DropTable(
                name: "VisitorAccessGrants");

            migrationBuilder.DropTable(
                name: "AccessRequests");

            migrationBuilder.DropTable(
                name: "DirectorySyncConfigs");

            migrationBuilder.DropTable(
                name: "PamPolicies");

            migrationBuilder.DropTable(
                name: "SecretEntries");

            migrationBuilder.DropTable(
                name: "AlertRules");

            migrationBuilder.DropTable(
                name: "SodConstraints");

            migrationBuilder.DropTable(
                name: "ServiceAccounts");

            migrationBuilder.DropTable(
                name: "Visitors");

            migrationBuilder.DropTable(
                name: "WorkflowTemplates");

            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Roles");
        }
    }
}
