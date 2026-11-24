using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds RefreshTokens.FamilyId: the sign-in (device session) a refresh token belongs to. A replayed
    /// token then ends only its own session family instead of every session of the user. Existing rows
    /// are backfilled with their own id (each legacy token becomes a family of one).
    /// Mirror of docs/manual-migrations/20261122120000_AddRefreshTokenFamilyId.sql.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261122120000_AddRefreshTokenFamilyId")]
    public partial class AddRefreshTokenFamilyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""RefreshTokens"" ADD COLUMN IF NOT EXISTS ""FamilyId"" uuid NULL;");
            migrationBuilder.Sql(@"UPDATE ""RefreshTokens"" SET ""FamilyId"" = ""Id"" WHERE ""FamilyId"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""RefreshTokens"" DROP COLUMN IF EXISTS ""FamilyId"";");
        }
    }
}
