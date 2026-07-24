using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOtpAndRiskTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "IX_RiskThresholds_TenantId",
                table: "RiskThresholds",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskThresholds_TenantId_IsActive",
                table: "RiskThresholds",
                columns: new[] { "TenantId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoginRiskScores");

            migrationBuilder.DropTable(
                name: "OtpCodes");

            migrationBuilder.DropTable(
                name: "RiskThresholds");
        }
    }
}
