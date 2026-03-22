using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyTesting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PolicyTests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    DraftPolicyJson = table.Column<string>(type: "jsonb", nullable: true),
                    TestScenarios = table.Column<string>(type: "jsonb", nullable: false),
                    ExpectedResults = table.Column<string>(type: "jsonb", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyTests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyTests_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PolicyTests_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PolicyTestResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExecutedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PassedCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    TotalCount = table.Column<int>(type: "integer", nullable: false),
                    DetailedResults = table.Column<string>(type: "jsonb", nullable: false),
                    ExecutionTimeMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyTestResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyTestResults_PolicyTests_PolicyTestId",
                        column: x => x.PolicyTestId,
                        principalTable: "PolicyTests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTestResults_ExecutedAt",
                table: "PolicyTestResults",
                column: "ExecutedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTestResults_PolicyTestId",
                table: "PolicyTestResults",
                column: "PolicyTestId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTestResults_Status_ExecutedAt",
                table: "PolicyTestResults",
                columns: new[] { "Status", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTests_IsActive",
                table: "PolicyTests",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTests_PolicyId",
                table: "PolicyTests",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTests_TenantId",
                table: "PolicyTests",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyTestResults");

            migrationBuilder.DropTable(
                name: "PolicyTests");
        }
    }
}
