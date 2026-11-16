using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZimraEGS.Data.Migrations
{
    /// <inheritdoc />
    public partial class Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaidUntilUtc",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    FailedSignIns = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSignInUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceRowId = table.Column<int>(type: "INTEGER", nullable: false),
                    Months = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtraDays = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodFromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PeriodToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedBy = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionPayments_Devices_DeviceRowId",
                        column: x => x.DeviceRowId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_AccountId",
                table: "Devices",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Email",
                table: "Accounts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_DeviceRowId",
                table: "SubscriptionPayments",
                column: "DeviceRowId");

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Accounts_AccountId",
                table: "Devices",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Devices_Accounts_AccountId",
                table: "Devices");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "SubscriptionPayments");

            migrationBuilder.DropIndex(
                name: "IX_Devices_AccountId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PaidUntilUtc",
                table: "Devices");
        }
    }
}
