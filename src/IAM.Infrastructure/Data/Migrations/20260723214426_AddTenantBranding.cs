using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantBrandings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LogoUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PrimaryColor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SecondaryColor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BackgroundUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CustomCss = table.Column<string>(type: "text", nullable: true),
                    EmailHeaderHtml = table.Column<string>(type: "text", nullable: true),
                    EmailFooterHtml = table.Column<string>(type: "text", nullable: true),
                    FaviconUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LoginTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LoginSubtitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WhiteLabelEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CustomDomain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBrandings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantBrandings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBrandings_CustomDomain",
                table: "TenantBrandings",
                column: "CustomDomain",
                unique: true,
                filter: "\"CustomDomain\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBrandings_TenantId",
                table: "TenantBrandings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantBrandings");
        }
    }
}
