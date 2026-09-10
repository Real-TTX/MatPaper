using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentOwnershipAndSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCommon",
                table: "Document",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "OwnerId",
                table: "Document",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewState",
                table: "Document",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DocumentShare",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_DocumentShare", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentShare_Document_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentShare_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Document_OwnerId",
                table: "Document",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShare_DocumentId_UserId",
                table: "DocumentShare",
                columns: new[] { "DocumentId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShare_UserId",
                table: "DocumentShare",
                column: "UserId");

            // Backfill existing documents: they predate the review workflow, so mark
            // them as already reviewed (do not flood the new inbox), and give them an
            // owner — their creator when that user still exists, otherwise the first
            // active administrator so nothing becomes silently invisible.
            migrationBuilder.Sql(@"UPDATE ""Document"" SET ""ReviewState"" = 1;");
            migrationBuilder.Sql(@"
                UPDATE ""Document"" SET ""OwnerId"" = ""CreateUserId""
                WHERE ""OwnerId"" IS NULL
                  AND ""CreateUserId"" IN (SELECT ""Id"" FROM ""User"");");
            migrationBuilder.Sql(@"
                UPDATE ""Document"" SET ""OwnerId"" = (
                    SELECT u.""Id"" FROM ""User"" u
                    JOIN ""Role"" r ON r.""Id"" = u.""RoleId""
                    WHERE r.""Name"" = 'Admin' AND u.""IsActive"" = TRUE
                    ORDER BY u.""Id"" LIMIT 1)
                WHERE ""OwnerId"" IS NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_Document_User_OwnerId",
                table: "Document",
                column: "OwnerId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Document_User_OwnerId",
                table: "Document");

            migrationBuilder.DropTable(
                name: "DocumentShare");

            migrationBuilder.DropIndex(
                name: "IX_Document_OwnerId",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "IsCommon",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "ReviewState",
                table: "Document");
        }
    }
}
