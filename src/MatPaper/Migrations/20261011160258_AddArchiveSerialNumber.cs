using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveSerialNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ArchiveSerialNumber",
                table: "Document",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Document_ArchiveSerialNumber",
                table: "Document",
                column: "ArchiveSerialNumber",
                unique: true,
                filter: "\"ArchiveSerialNumber\" IS NOT NULL AND \"UpdateState\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Document_ArchiveSerialNumber",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "ArchiveSerialNumber",
                table: "Document");
        }
    }
}
