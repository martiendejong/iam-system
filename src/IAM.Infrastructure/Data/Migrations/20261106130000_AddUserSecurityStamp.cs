using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 5490: per-user security stamp for the IAM.Session single sign-on cookie. IAM creates its
    /// schema with EnsureCreated (a no-op on an existing database), so apply
    /// docs/manual-migrations/20261106130000_AddUserSecurityStamp.sql to iam_db BEFORE starting a build
    /// that contains this change; it is idempotent and purely additive. Existing rows get a random stamp.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261106130000_AddUserSecurityStamp")]
    public partial class AddUserSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"SecurityStamp\" = md5(random()::text || clock_timestamp()::text || \"Id\"::text) WHERE \"SecurityStamp\" = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");
        }
    }
}
