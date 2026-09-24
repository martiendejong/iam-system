using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyScopeAndVaultReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keys issued before scopes existed could already call every API-key endpoint (app-role register/read),
            // so they backfill to "write" - the same reach, no more. Nothing is backfilled to "admin".
            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "ApiKeys",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "write");

            migrationBuilder.AddColumn<string>(
                name: "VaultReference",
                table: "ApiKeys",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Scope",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "VaultReference",
                table: "ApiKeys");
        }
    }
}
