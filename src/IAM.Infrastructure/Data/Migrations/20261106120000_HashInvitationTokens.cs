using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 5578: Invitations.Token stores SHA-256 hex of the e-mailed token. Hashes existing
    /// raw tokens in place; idempotent (values that already are 64 hex chars are skipped).
    /// Mirror of docs/manual-migrations/20261106120000_HashInvitationTokens.sql.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261106120000_HashInvitationTokens")]
    public partial class HashInvitationTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"UPDATE ""Invitations"" SET ""Token"" = encode(sha256(convert_to(""Token"", 'UTF8')), 'hex') WHERE ""Token"" !~ '^[0-9a-f]{64}$';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Hashing is one-way; the raw tokens cannot be restored.
        }
    }
}
