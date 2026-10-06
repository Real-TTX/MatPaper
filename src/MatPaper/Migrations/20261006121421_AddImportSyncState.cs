using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddImportSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SyncState",
                table: "ImportTask",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncState",
                table: "ImportTask");
        }
    }
}
