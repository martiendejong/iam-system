using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTemporalPolicyEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleTemplateId",
                table: "Policies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HolidayCalendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    Timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Holidays = table.Column<string>(type: "jsonb", nullable: false),
                    IsAutoUpdated = table.Column<bool>(type: "boolean", nullable: false),
                    ExternalSourceUrl = table.Column<string>(type: "text", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HolidayCalendars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HolidayCalendars_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PolicyBehavior = table.Column<int>(type: "integer", nullable: false),
                    SendNotifications = table.Column<bool>(type: "boolean", nullable: false),
                    NotificationLeadTimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsRecurring = table.Column<bool>(type: "boolean", nullable: false),
                    RecurrencePattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ActualStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActualEndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceWindows_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TemporaryAccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Justification = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxUseCount = table.Column<int>(type: "integer", nullable: true),
                    CurrentUseCount = table.Column<int>(type: "integer", nullable: false),
                    NotifyOnExpiration = table.Column<bool>(type: "boolean", nullable: false),
                    AutoExtendIfActive = table.Column<bool>(type: "boolean", nullable: false),
                    ExtensionDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxExtensions = table.Column<int>(type: "integer", nullable: false),
                    CurrentExtensions = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FirstUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocationReason = table.Column<string>(type: "text", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemporaryAccessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemporaryAccessGrants_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TemporaryAccessGrants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TemporaryAccessGrants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScheduleType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecurrencePattern = table.Column<string>(type: "jsonb", nullable: false),
                    ExceptionDates = table.Column<string>(type: "jsonb", nullable: true),
                    InclusionDates = table.Column<string>(type: "jsonb", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HolidayCalendarId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleTemplates_HolidayCalendars_HolidayCalendarId",
                        column: x => x.HolidayCalendarId,
                        principalTable: "HolidayCalendars",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ScheduleTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Policies_ScheduleTemplateId",
                table: "Policies",
                column: "ScheduleTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_HolidayCalendars_CountryCode",
                table: "HolidayCalendars",
                column: "CountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_HolidayCalendars_IsActive_IsAutoUpdated",
                table: "HolidayCalendars",
                columns: new[] { "IsActive", "IsAutoUpdated" });

            migrationBuilder.CreateIndex(
                name: "IX_HolidayCalendars_TenantId",
                table: "HolidayCalendars",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_StartTime",
                table: "MaintenanceWindows",
                column: "StartTime");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_Status_StartTime_EndTime",
                table: "MaintenanceWindows",
                columns: new[] { "Status", "StartTime", "EndTime" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_TenantId",
                table: "MaintenanceWindows",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleTemplates_HolidayCalendarId",
                table: "ScheduleTemplates",
                column: "HolidayCalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleTemplates_ScheduleType_IsActive",
                table: "ScheduleTemplates",
                columns: new[] { "ScheduleType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleTemplates_TenantId",
                table: "ScheduleTemplates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryAccessGrants_RoleId",
                table: "TemporaryAccessGrants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryAccessGrants_Status_StartTime_EndTime",
                table: "TemporaryAccessGrants",
                columns: new[] { "Status", "StartTime", "EndTime" });

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryAccessGrants_TenantId",
                table: "TemporaryAccessGrants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryAccessGrants_UserId",
                table: "TemporaryAccessGrants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryAccessGrants_UserId_Status_EndTime",
                table: "TemporaryAccessGrants",
                columns: new[] { "UserId", "Status", "EndTime" });

            migrationBuilder.AddForeignKey(
                name: "FK_Policies_ScheduleTemplates_ScheduleTemplateId",
                table: "Policies",
                column: "ScheduleTemplateId",
                principalTable: "ScheduleTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Policies_ScheduleTemplates_ScheduleTemplateId",
                table: "Policies");

            migrationBuilder.DropTable(
                name: "MaintenanceWindows");

            migrationBuilder.DropTable(
                name: "ScheduleTemplates");

            migrationBuilder.DropTable(
                name: "TemporaryAccessGrants");

            migrationBuilder.DropTable(
                name: "HolidayCalendars");

            migrationBuilder.DropIndex(
                name: "IX_Policies_ScheduleTemplateId",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "ScheduleTemplateId",
                table: "Policies");
        }
    }
}
