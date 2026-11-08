using System;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 5852: persisted per-account failure signals and alert marks. Purely additive. See
    /// docs/manual-migrations/20261106120000_AddLoginFailureSignals.sql for the SQL applied to iam_db.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261106120000_AddLoginFailureSignals")]
    public partial class AddLoginFailureSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoginFailureSignals",
                columns: table => new
                {
                    AccountKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastFailureAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_LoginFailureSignals", x => new { x.AccountKey, x.SourceKey }));

            migrationBuilder.CreateIndex("IX_LoginFailureSignals_LastFailureAt", "LoginFailureSignals", "LastFailureAt");
            migrationBuilder.CreateIndex("IX_LoginFailureSignals_AccountKey_LastFailureAt", "LoginFailureSignals", new[] { "AccountKey", "LastFailureAt" });

            migrationBuilder.CreateTable(
                name: "AccountGuessingAlertMarks",
                columns: table => new
                {
                    AccountKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AlertedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AccountGuessingAlertMarks", x => x.AccountKey));

            migrationBuilder.CreateIndex("IX_AccountGuessingAlertMarks_AlertedAt", "AccountGuessingAlertMarks", "AlertedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LoginFailureSignals");
            migrationBuilder.DropTable(name: "AccountGuessingAlertMarks");
        }
    }
}
