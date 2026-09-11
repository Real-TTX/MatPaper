using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddCorrespondentContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Correspondent",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Correspondent",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Correspondent",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Address",
                table: "Correspondent");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Correspondent");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Correspondent");
        }
    }
}
