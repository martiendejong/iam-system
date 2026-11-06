using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 5569: RefreshTokens.MfaCompleted records whether the sign-in that created a session completed a
    /// second factor, so the refresh path can refuse to extend a session that skipped a factor the user's
    /// organization now requires. IAM creates its schema with EnsureCreated (a no-op on an existing
    /// database), so apply docs/manual-migrations/20261106150000_AddMfaCompletedToRefreshTokens.sql to
    /// iam_db BEFORE starting a build that contains this change; it is idempotent. Existing sessions get
    /// false, which only matters to members of a Require-MFA organization (they sign in once more).
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261106150000_AddMfaCompletedToRefreshTokens")]
    public partial class AddMfaCompletedToRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MfaCompleted",
                table: "RefreshTokens",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MfaCompleted",
                table: "RefreshTokens");
        }
    }
}
