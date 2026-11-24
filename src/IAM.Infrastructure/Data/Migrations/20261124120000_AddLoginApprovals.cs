using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds LoginApprovals and WebPushSubscriptions (approve a new browser sign-in from a signed-in device).
    /// Idempotent mirror of docs/manual-migrations/20261124120000_AddLoginApprovals.sql.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261124120000_AddLoginApprovals")]
    public partial class AddLoginApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""LoginApprovals"" (
    ""Id"" uuid NOT NULL PRIMARY KEY,
    ""PollSecretHash"" varchar(64) NOT NULL,
    ""UserId"" uuid NULL,
    ""Status"" integer NOT NULL,
    ""MatchNumber"" integer NOT NULL,
    ""FailedMatches"" integer NOT NULL,
    ""RememberMe"" boolean NOT NULL,
    ""RequestIp"" varchar(50) NULL,
    ""RequestUserAgent"" varchar(500) NULL,
    ""RequestDevice"" varchar(100) NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""ExpiresAt"" timestamp with time zone NOT NULL,
    ""DecidedAt"" timestamp with time zone NULL,
    ""ConsumedAt"" timestamp with time zone NULL,
    ""ApprovedByUserId"" uuid NULL,
    ""ApprovedByRefreshTokenId"" uuid NULL,
    ""Version"" integer NOT NULL
);
CREATE INDEX IF NOT EXISTS ""IX_LoginApprovals_UserId_Status_ExpiresAt"" ON ""LoginApprovals"" (""UserId"", ""Status"", ""ExpiresAt"");
CREATE INDEX IF NOT EXISTS ""IX_LoginApprovals_CreatedAt"" ON ""LoginApprovals"" (""CreatedAt"");

CREATE TABLE IF NOT EXISTS ""WebPushSubscriptions"" (
    ""Id"" uuid NOT NULL PRIMARY KEY,
    ""UserId"" uuid NOT NULL,
    ""Endpoint"" varchar(1000) NOT NULL,
    ""P256dh"" varchar(200) NOT NULL,
    ""Auth"" varchar(100) NOT NULL,
    ""Label"" varchar(100) NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""LastSuccessAt"" timestamp with time zone NULL,
    ""FailureCount"" integer NOT NULL
);
CREATE INDEX IF NOT EXISTS ""IX_WebPushSubscriptions_UserId"" ON ""WebPushSubscriptions"" (""UserId"");
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_WebPushSubscriptions_Endpoint"" ON ""WebPushSubscriptions"" (""Endpoint"");
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the tables are additive and an older build ignores them, so rolling back the
            // application never needs the tables removed.
        }
    }
}
