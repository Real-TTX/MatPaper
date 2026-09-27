using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatPaper.Migrations
{
    /// <summary>
    /// Merges the storage-scan candidate list into the inbox: a found file is a pending
    /// document now, so <c>InboxItem</c> disappears. Existing candidates are carried over
    /// (dismissed ones as ignored documents, which keeps them out of the next search).
    /// </summary>
    public partial class UnifyInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----- New columns ------------------------------------------------------

            migrationBuilder.AddColumn<bool>(
                name: "DefaultIsCommon",
                table: "StorageLocation",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "DefaultOwnerId",
                table: "StorageLocation",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastScanError",
                table: "StorageLocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastScanFound",
                table: "StorageLocation",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastScanUtc",
                table: "StorageLocation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanExtensions",
                table: "StorageLocation",
                type: "text",
                nullable: false,
                defaultValue: "");

            // A C# property initializer does not reach rows that already exist.
            migrationBuilder.Sql(@"
                UPDATE ""StorageLocation""
                SET ""ScanExtensions"" = '.pdf,.png,.jpg,.jpeg,.tif,.tiff'
                WHERE ""ScanExtensions"" = '';");

            migrationBuilder.AddColumn<DateTime>(
                name: "FileModifiedUtc",
                table: "Document",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "Document",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // ----- Indexes ----------------------------------------------------------

            migrationBuilder.DropIndex(
                name: "IX_Document_StorageLocationId",
                table: "Document");

            migrationBuilder.CreateIndex(
                name: "IX_StorageLocation_DefaultOwnerId",
                table: "StorageLocation",
                column: "DefaultOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Document_ContentHash",
                table: "Document",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Document_OwnerId_ReviewState_UpdateState",
                table: "Document",
                columns: new[] { "OwnerId", "ReviewState", "UpdateState" });

            migrationBuilder.CreateIndex(
                name: "IX_Document_StorageLocationId_RelativePath",
                table: "Document",
                columns: new[] { "StorageLocationId", "RelativePath" });

            migrationBuilder.AddForeignKey(
                name: "FK_StorageLocation_User_DefaultOwnerId",
                table: "StorageLocation",
                column: "DefaultOwnerId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ----- Carry the old candidates over ------------------------------------

            // Documents that were adopted "keep in place" by the old import are exactly
            // the non-staged pending ones; label them as found so the new inbox and the
            // archive filter treat them correctly.
            migrationBuilder.Sql(@"
                UPDATE ""Document""
                SET ""Origin"" = 4
                WHERE NOT ""IsStaged"" AND ""ReviewState"" = 0;");

            // InboxItem.UpdateState: 1 = waiting, 0 = dismissed or already imported.
            // Waiting becomes a pending document, dismissed an ignored one — both keep
            // the next search from offering the file again. The guards skip candidates
            // whose file is already a document (same path, or same content).
            migrationBuilder.Sql(@"
                INSERT INTO ""Document""
                    (""Token"", ""Title"", ""OriginalFileName"", ""RelativePath"", ""StorageLocationId"",
                     ""IsStaged"", ""FileSize"", ""ContentHash"", ""FileModifiedUtc"", ""Origin"",
                     ""ReviewState"", ""OcrState"", ""UpdateState"", ""OwnerId"", ""IsCommon"",
                     ""PageCount"", ""CreateDate"", ""UpdateDate"", ""CreateUserId"", ""UpdateUserId"")
                SELECT gen_random_uuid(),
                       regexp_replace(i.""FileName"", '\.[^.]*$', ''),
                       i.""FileName"",
                       i.""RelativePath"",
                       i.""StorageLocationId"",
                       false,
                       i.""FileSize"",
                       i.""ContentHash"",
                       i.""FileModifiedUtc"",
                       4,
                       CASE WHEN i.""UpdateState"" = 1 THEN 0 ELSE 2 END,
                       3,
                       1,
                       (SELECT min(u.""Id"") FROM ""User"" u
                          JOIN ""Role"" r ON r.""Id"" = u.""RoleId""
                         WHERE u.""IsActive"" AND r.""Name"" = 'Admin'),
                       false,
                       0,
                       i.""CreateDate"",
                       now(),
                       NULL,
                       NULL
                FROM ""InboxItem"" i
                WHERE NOT EXISTS (
                        SELECT 1 FROM ""Document"" d
                         WHERE d.""StorageLocationId"" = i.""StorageLocationId""
                           AND d.""RelativePath"" = i.""RelativePath""
                           AND NOT d.""IsStaged"")
                  AND NOT EXISTS (
                        SELECT 1 FROM ""Document"" d2
                         WHERE i.""ContentHash"" IS NOT NULL
                           AND d2.""ContentHash"" = i.""ContentHash""
                           AND d2.""UpdateState"" <> 0);");

            migrationBuilder.DropTable(
                name: "InboxItem");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The new states have no meaning for the old code: fold them back.
            migrationBuilder.Sql(@"
                DELETE FROM ""Document"" WHERE ""Origin"" = 4 AND ""ReviewState"" = 2;
                UPDATE ""Document"" SET ""ReviewState"" = 0 WHERE ""ReviewState"" = 2;
                UPDATE ""Document"" SET ""OcrState"" = 0 WHERE ""OcrState"" = 3;");

            migrationBuilder.DropForeignKey(
                name: "FK_StorageLocation_User_DefaultOwnerId",
                table: "StorageLocation");

            migrationBuilder.DropIndex(
                name: "IX_StorageLocation_DefaultOwnerId",
                table: "StorageLocation");

            migrationBuilder.DropIndex(
                name: "IX_Document_ContentHash",
                table: "Document");

            migrationBuilder.DropIndex(
                name: "IX_Document_OwnerId_ReviewState_UpdateState",
                table: "Document");

            migrationBuilder.DropIndex(
                name: "IX_Document_StorageLocationId_RelativePath",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "DefaultIsCommon",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "DefaultOwnerId",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "LastScanError",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "LastScanFound",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "LastScanUtc",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "ScanExtensions",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "FileModifiedUtc",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Document");

            migrationBuilder.CreateTable(
                name: "InboxItem",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StorageLocationId = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    FileModifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    RelativePath = table.Column<string>(type: "text", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateState = table.Column<int>(type: "integer", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboxItem_StorageLocation_StorageLocationId",
                        column: x => x.StorageLocationId,
                        principalTable: "StorageLocation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Document_StorageLocationId",
                table: "Document",
                column: "StorageLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxItem_StorageLocationId_RelativePath",
                table: "InboxItem",
                columns: new[] { "StorageLocationId", "RelativePath" },
                unique: true);
        }
    }
}
