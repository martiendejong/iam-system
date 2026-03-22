using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmergencyOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmergencyOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OverrideType = table.Column<int>(type: "integer", nullable: false),
                    Justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AccessedResources = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ActionsPerformed = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeactivatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewComments = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IncidentTicketId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    SecurityNotified = table.Column<bool>(type: "boolean", nullable: false),
                    SecurityNotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmergencyOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmergencyOverrides_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EmergencyOverrides_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyOverrides_ApprovalStatus",
                table: "EmergencyOverrides",
                column: "ApprovalStatus");

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyOverrides_SecurityNotified",
                table: "EmergencyOverrides",
                column: "SecurityNotified");

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyOverrides_Severity",
                table: "EmergencyOverrides",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyOverrides_Status_ActivatedAt",
                table: "EmergencyOverrides",
                columns: new[] { "Status", "ActivatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyOverrides_Status_ExpiresAt",
                table: "EmergencyOverrides",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyOverrides_TenantId",
                table: "EmergencyOverrides",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyOverrides_UserId",
                table: "EmergencyOverrides",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyOverrides_UserId_Status_ActivatedAt",
                table: "EmergencyOverrides",
                columns: new[] { "UserId", "Status", "ActivatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmergencyOverrides");
        }
    }
}
