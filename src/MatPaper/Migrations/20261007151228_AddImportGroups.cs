using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatPaper.Migrations
{
    /// <inheritdoc />
    public partial class AddImportGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GroupId",
                table: "ImportTask",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "ImportTask",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ImportGroup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CronExpression = table.Column<string>(type: "text", nullable: true),
                    StorageLocationId = table.Column<long>(type: "bigint", nullable: true),
                    OwnerUserId = table.Column<long>(type: "bigint", nullable: true),
                    IsCommon = table.Column<bool>(type: "boolean", nullable: false),
                    SkipInbox = table.Column<bool>(type: "boolean", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    TagIds = table.Column<string>(type: "text", nullable: true),
                    UpdateState = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportGroup", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportGroup");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "ImportTask");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "ImportTask");
        }
    }
}
