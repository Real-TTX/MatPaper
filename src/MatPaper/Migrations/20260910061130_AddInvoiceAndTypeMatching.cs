using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceAndTypeMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchPattern",
                table: "DocumentType",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceNumber",
                table: "Document",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 1L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 2L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 3L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 4L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 5L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 6L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 7L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 8L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 9L,
                column: "MatchPattern",
                value: null);

            migrationBuilder.UpdateData(
                table: "DocumentType",
                keyColumn: "Id",
                keyValue: 10L,
                column: "MatchPattern",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchPattern",
                table: "DocumentType");

            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                table: "Document");
        }
    }
}
