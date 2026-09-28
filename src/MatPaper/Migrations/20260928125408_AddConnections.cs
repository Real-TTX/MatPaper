using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BasePath",
                table: "StorageLocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ConnectionId",
                table: "StorageLocation",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Connection",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AuthMode = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    Domain = table.Column<string>(type: "text", nullable: true),
                    ProtectedPassword = table.Column<string>(type: "text", nullable: false),
                    OAuthClientId = table.Column<string>(type: "text", nullable: true),
                    ProtectedClientSecret = table.Column<string>(type: "text", nullable: false),
                    ProtectedRefreshToken = table.Column<string>(type: "text", nullable: false),
                    OAuthScopes = table.Column<string>(type: "text", nullable: true),
                    OAuthConnectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateState = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connection", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageLocation_ConnectionId",
                table: "StorageLocation",
                column: "ConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_StorageLocation_Connection_ConnectionId",
                table: "StorageLocation",
                column: "ConnectionId",
                principalTable: "Connection",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StorageLocation_Connection_ConnectionId",
                table: "StorageLocation");

            migrationBuilder.DropTable(
                name: "Connection");

            migrationBuilder.DropIndex(
                name: "IX_StorageLocation_ConnectionId",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "BasePath",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "ConnectionId",
                table: "StorageLocation");
        }
    }
}
