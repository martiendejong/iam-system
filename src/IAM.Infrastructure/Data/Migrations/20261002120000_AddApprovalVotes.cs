using System;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Task 4711: one row per (approval step, approver) so a quorum counts distinct people.
    /// Purely additive: ApprovalSteps.ApprovalsReceived is kept untouched for in-flight steps.
    /// </summary>
    [DbContext(typeof(IAMDbContext))]
    [Migration("20261002120000_AddApprovalVotes")]
    public partial class AddApprovalVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalVotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalVotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalVotes_ApprovalSteps_ApprovalStepId",
                        column: x => x.ApprovalStepId,
                        principalTable: "ApprovalSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApprovalVotes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalVotes_ApprovalStepId_UserId",
                table: "ApprovalVotes",
                columns: new[] { "ApprovalStepId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalVotes_UserId",
                table: "ApprovalVotes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalVotes");
        }
    }
}
