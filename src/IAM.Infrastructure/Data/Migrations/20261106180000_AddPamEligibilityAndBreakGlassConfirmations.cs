using System;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 5629: PAM becomes deny-by-default. Adds PamPolicies.EligibleRoleId (the role a caller must hold in
    /// the tenant) and PrivilegedSessions.BreakGlassConfirmations (named approvers who have confirmed).
    /// Purely additive; both columns are nullable. IAM creates its schema with EnsureCreated and never calls
    /// Migrate(), so apply docs/manual-migrations/20261106180000_AddPamEligibilityAndBreakGlassConfirmations.sql
    /// to iam_db BEFORE starting a build that contains this change; it is idempotent.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261106180000_AddPamEligibilityAndBreakGlassConfirmations")]
    public partial class AddPamEligibilityAndBreakGlassConfirmations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EligibleRoleId",
                table: "PamPolicies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BreakGlassConfirmations",
                table: "PrivilegedSessions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "EligibleRoleId", table: "PamPolicies");
            migrationBuilder.DropColumn(name: "BreakGlassConfirmations", table: "PrivilegedSessions");
        }
    }
}
