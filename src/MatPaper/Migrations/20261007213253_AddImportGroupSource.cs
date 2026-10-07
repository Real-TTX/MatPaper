using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddImportGroupSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceConnectionId",
                table: "ImportGroup",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePath",
                table: "ImportGroup",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceShare",
                table: "ImportGroup",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceType",
                table: "ImportGroup",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                table: "ImportGroup");

            migrationBuilder.DropColumn(
                name: "SourcePath",
                table: "ImportGroup");

            migrationBuilder.DropColumn(
                name: "SourceShare",
                table: "ImportGroup");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "ImportGroup");
        }
    }
}
