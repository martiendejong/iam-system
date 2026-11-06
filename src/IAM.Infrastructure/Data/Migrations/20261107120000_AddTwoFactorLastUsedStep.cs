using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 5491: last accepted TOTP time step per user (replay guard). Purely additive. IAM creates its
    /// schema with EnsureCreated, so apply docs/manual-migrations/20261107120000_AddTwoFactorLastUsedStep.sql
    /// to iam_db BEFORE starting a build that contains this change; it is idempotent.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261107120000_AddTwoFactorLastUsedStep")]
    public partial class AddTwoFactorLastUsedStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TwoFactorLastUsedStep",
                table: "Users",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TwoFactorLastUsedStep",
                table: "Users");
        }
    }
}
