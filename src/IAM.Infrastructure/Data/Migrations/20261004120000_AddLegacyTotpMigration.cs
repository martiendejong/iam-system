using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 3162: TOTP moved from HMAC-SHA1 to HMAC-SHA256 (PR #104), so authenticator apps enrolled before it no longer
    /// work. <c>Users.TotpAlgorithm</c> marks enrollments made for SHA-256 (NULL on a TOTP user = legacy), and
    /// <c>OrganizationSettings.LegacyTotpMigration</c> is the tenant's policy for those legacy enrollments
    /// (0 = e-mail PIN, 1 = off). Purely additive: no existing row is changed, every default is the intended one.
    /// </summary>
    /// <inheritdoc />
    public partial class AddLegacyTotpMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TotpAlgorithm",
                table: "Users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LegacyTotpMigration",
                table: "OrganizationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotpAlgorithm",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LegacyTotpMigration",
                table: "OrganizationSettings");
        }
    }
}
