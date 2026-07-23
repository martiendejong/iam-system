using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectorySync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncConfigs_TenantId",
                table: "DirectorySyncConfigs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncConfigs_IsActive_LastSyncAt",
                table: "DirectorySyncConfigs",
                columns: new[] { "IsActive", "LastSyncAt" });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectorySyncLogs");

            migrationBuilder.DropTable(
                name: "DirectorySyncConfigs");
        }
    }
}
