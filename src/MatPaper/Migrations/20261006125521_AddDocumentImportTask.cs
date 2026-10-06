using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentImportTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ImportTaskId",
                table: "Document",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Document_ImportTaskId",
                table: "Document",
                column: "ImportTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Document_ImportTaskId",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "ImportTaskId",
                table: "Document");
        }
    }
}
