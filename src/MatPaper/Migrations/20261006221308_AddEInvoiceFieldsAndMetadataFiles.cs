using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddEInvoiceFieldsAndMetadataFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WriteMetadataFiles",
                table: "StorageLocation",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BuyerName",
                table: "Document",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerReference",
                table: "Document",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Document",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DueDate",
                table: "Document",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GrossAmount",
                table: "Document",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasEInvoiceXml",
                table: "Document",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsEInvoice",
                table: "Document",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "NetAmount",
                table: "Document",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerIban",
                table: "Document",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerVatId",
                table: "Document",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "Document",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WriteMetadataFiles",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "BuyerName",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "BuyerReference",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "GrossAmount",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "HasEInvoiceXml",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "IsEInvoice",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "NetAmount",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "SellerIban",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "SellerVatId",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "Document");
        }
    }
}
