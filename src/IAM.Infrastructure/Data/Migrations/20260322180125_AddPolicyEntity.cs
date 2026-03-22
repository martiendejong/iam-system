using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    InheritanceScope = table.Column<int>(type: "integer", nullable: false),
                    InheritedFromPolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resource = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Effect = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    TimeConstraints = table.Column<string>(type: "jsonb", nullable: true),
                    Conditions = table.Column<string>(type: "jsonb", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Policies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Policies_Policies_InheritedFromPolicyId",
                        column: x => x.InheritedFromPolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Policies_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Policies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Policies_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Policies_InheritedFromPolicyId",
                table: "Policies",
                column: "InheritedFromPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Policies_Resource_Action",
                table: "Policies",
                columns: new[] { "Resource", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_Policies_RoleId",
                table: "Policies",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Policies_TenantId",
                table: "Policies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Policies_TenantId_IsActive_ExpiresAt",
                table: "Policies",
                columns: new[] { "TenantId", "IsActive", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Policies_UserId",
                table: "Policies",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Policies");
        }
    }
}
