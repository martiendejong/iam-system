using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdatedIAMSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use raw SQL for type conversion with USING clause
            migrationBuilder.Sql(
                @"ALTER TABLE ""Tenants"" ALTER COLUMN ""Settings"" TYPE jsonb USING ""Settings""::jsonb;");

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "Tenants",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "Tenants");

            migrationBuilder.AlterColumn<string>(
                name: "Settings",
                table: "Tenants",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }
    }
}
