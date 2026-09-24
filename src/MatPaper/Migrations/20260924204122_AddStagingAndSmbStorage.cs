using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddStagingAndSmbStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CredentialId",
                table: "StorageLocation",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "StorageLocation",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SmbHost",
                table: "StorageLocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbPath",
                table: "StorageLocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbShare",
                table: "StorageLocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsStaged",
                table: "Document",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_StorageLocation_CredentialId",
                table: "StorageLocation",
                column: "CredentialId");

            migrationBuilder.AddForeignKey(
                name: "FK_StorageLocation_Credential_CredentialId",
                table: "StorageLocation",
                column: "CredentialId",
                principalTable: "Credential",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StorageLocation_Credential_CredentialId",
                table: "StorageLocation");

            migrationBuilder.DropIndex(
                name: "IX_StorageLocation_CredentialId",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "CredentialId",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "SmbHost",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "SmbPath",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "SmbShare",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "IsStaged",
                table: "Document");
        }
    }
}
