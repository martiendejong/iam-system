using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 4316: per-provider redirect URI allow-list. The column is text, not jsonb: the entity
    /// property is a plain JSON string (parsed in SocialAuthService) with no jsonb mapping in
    /// IAMDbContext, so EF binds it as text, and EnsureCreated and the live iam_db create text.
    /// Purely additive. Mirrors docs/manual-migrations/20260926120000_AddAllowedRedirectUrisToIdentityProvider.sql.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20260926120000_AddAllowedRedirectUrisToIdentityProvider")]
    public partial class AddAllowedRedirectUrisToIdentityProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedRedirectUris",
                table: "IdentityProviders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedRedirectUris",
                table: "IdentityProviders");
        }
    }
}
