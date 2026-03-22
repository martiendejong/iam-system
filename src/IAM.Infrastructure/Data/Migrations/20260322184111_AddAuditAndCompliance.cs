using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditAndCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComplianceReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Framework = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Findings = table.Column<string>(type: "jsonb", nullable: false),
                    Recommendations = table.Column<string>(type: "jsonb", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Statistics = table.Column<string>(type: "jsonb", nullable: false),
                    GeneratedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExportedFilePath = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComplianceReports_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PolicyAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resource = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WasAllowed = table.Column<bool>(type: "boolean", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DeviceId = table.Column<string>(type: "text", nullable: true),
                    UserAgent = table.Column<string>(type: "text", nullable: true),
                    Changes = table.Column<string>(type: "jsonb", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    EvaluationTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    PoliciesEvaluatedCount = table.Column<int>(type: "integer", nullable: true),
                    RiskScore = table.Column<int>(type: "integer", nullable: true),
                    TriggeredAlert = table.Column<bool>(type: "boolean", nullable: false),
                    AlertDetails = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyAuditEvents_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PolicyAuditEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PolicyAuditEvents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceReports_Framework_Status",
                table: "ComplianceReports",
                columns: new[] { "Framework", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceReports_GeneratedAt",
                table: "ComplianceReports",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceReports_PeriodStart_PeriodEnd",
                table: "ComplianceReports",
                columns: new[] { "PeriodStart", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceReports_TenantId",
                table: "ComplianceReports",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAuditEvents_CreatedAt",
                table: "PolicyAuditEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAuditEvents_EventType_CreatedAt",
                table: "PolicyAuditEvents",
                columns: new[] { "EventType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAuditEvents_PolicyId_CreatedAt",
                table: "PolicyAuditEvents",
                columns: new[] { "PolicyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAuditEvents_TenantId_CreatedAt",
                table: "PolicyAuditEvents",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAuditEvents_TriggeredAlert",
                table: "PolicyAuditEvents",
                column: "TriggeredAlert");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAuditEvents_UserId_CreatedAt",
                table: "PolicyAuditEvents",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComplianceReports");

            migrationBuilder.DropTable(
                name: "PolicyAuditEvents");
        }
    }
}
