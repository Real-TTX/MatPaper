using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectOwnershipAndSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCommon",
                table: "Project",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "OwnerId",
                table: "Project",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectShare",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CanEdit = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateState = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectShare", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectShare_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectShare_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Project_OwnerId",
                table: "Project",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectShare_ProjectId_UserId",
                table: "ProjectShare",
                columns: new[] { "ProjectId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectShare_UserId",
                table: "ProjectShare",
                column: "UserId");

            // Backfill existing projects with an owner — their creator when that user
            // still exists, otherwise the first active administrator — so no project
            // becomes silently invisible once ownership filtering applies.
            migrationBuilder.Sql(@"
                UPDATE ""Project"" SET ""OwnerId"" = ""CreateUserId""
                WHERE ""OwnerId"" IS NULL
                  AND ""CreateUserId"" IN (SELECT ""Id"" FROM ""User"");");
            migrationBuilder.Sql(@"
                UPDATE ""Project"" SET ""OwnerId"" = (
                    SELECT u.""Id"" FROM ""User"" u
                    JOIN ""Role"" r ON r.""Id"" = u.""RoleId""
                    WHERE r.""Name"" = 'Admin' AND u.""IsActive"" = TRUE
                    ORDER BY u.""Id"" LIMIT 1)
                WHERE ""OwnerId"" IS NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_Project_User_OwnerId",
                table: "Project",
                column: "OwnerId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Project_User_OwnerId",
                table: "Project");

            migrationBuilder.DropTable(
                name: "ProjectShare");

            migrationBuilder.DropIndex(
                name: "IX_Project_OwnerId",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "IsCommon",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Project");
        }
    }
}
