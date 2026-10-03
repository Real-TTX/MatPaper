using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThemeAccent",
                table: "User",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThemeMode",
                table: "User",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThemeScheme",
                table: "User",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThemeAccent",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ThemeMode",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ThemeScheme",
                table: "User");
        }
    }
}
