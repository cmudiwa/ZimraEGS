using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZimraEGS.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExcelBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExcelBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceRowId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ResultsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcelBatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExcelBatches_AccountId",
                table: "ExcelBatches",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExcelBatches_CreatedUtc",
                table: "ExcelBatches",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ExcelBatches_PublicId",
                table: "ExcelBatches",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExcelBatches");
        }
    }
}
