using System;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 4992: schema for task 4057 (PR #137), which added ManagerUserId + PrincipalKind to
    /// Users and ServiceAccounts but shipped no migration. IAM creates its schema with
    /// EnsureCreated (a no-op on an existing database), so a build containing #137 fails every
    /// Users query with "column u.ManagerUserId does not exist" until this SQL has been applied
    /// (the 2026-10-05 deploy of a005238 was rolled back for exactly that reason).
    /// Purely additive. Apply docs/manual-migrations/20261005120000_AddPrincipalKindAndManager.sql
    /// to iam_db BEFORE starting a build that contains #137; it is idempotent.
    /// Defaults match the entity initializers: users Human (0), service accounts Service (2).
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261005120000_AddPrincipalKindAndManager")]
    public partial class AddPrincipalKindAndManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ManagerUserId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrincipalKind",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ManagerUserId",
                table: "ServiceAccounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrincipalKind",
                table: "ServiceAccounts",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ManagerUserId",
                table: "Users",
                column: "ManagerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_ManagerUserId",
                table: "ServiceAccounts",
                column: "ManagerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_ManagerUserId",
                table: "Users",
                column: "ManagerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceAccounts_Users_ManagerUserId",
                table: "ServiceAccounts",
                column: "ManagerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceAccounts_Users_ManagerUserId",
                table: "ServiceAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_ManagerUserId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_ServiceAccounts_ManagerUserId",
                table: "ServiceAccounts");

            migrationBuilder.DropIndex(
                name: "IX_Users_ManagerUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PrincipalKind",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "ManagerUserId",
                table: "ServiceAccounts");

            migrationBuilder.DropColumn(
                name: "PrincipalKind",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ManagerUserId",
                table: "Users");
        }
    }
}
