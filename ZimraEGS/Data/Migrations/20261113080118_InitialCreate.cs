using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZimraEGS.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceID = table.Column<int>(type: "INTEGER", nullable: false),
                    Environment = table.Column<string>(type: "TEXT", nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    LinkTokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPfx = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceCertificate = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedCsr = table.Column<string>(type: "TEXT", nullable: false),
                    CertificateValidTillUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InfoJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigFetchedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FiscalDayStatus = table.Column<string>(type: "TEXT", nullable: false),
                    FiscalDayNo = table.Column<int>(type: "INTEGER", nullable: false),
                    FiscalDayOpened = table.Column<string>(type: "TEXT", nullable: true),
                    LastReceiptCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReceiptGlobalNo = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReceiptHash = table.Column<string>(type: "TEXT", nullable: false),
                    CarriedSummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    CarriedSummaryFiscalDayNo = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCloseDayError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Receipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceRowId = table.Column<int>(type: "INTEGER", nullable: false),
                    FormKey = table.Column<string>(type: "TEXT", nullable: false),
                    FiscalDayNo = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceiptCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceiptGlobalNo = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceiptType = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    InvoiceNo = table.Column<string>(type: "TEXT", nullable: false),
                    ReceiptDate = table.Column<string>(type: "TEXT", nullable: false),
                    Total = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReceiptJson = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", nullable: false),
                    QrCode = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationCode = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    FdmsReceiptId = table.Column<long>(type: "INTEGER", nullable: true),
                    ResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    RefJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Receipts_Devices_DeviceRowId",
                        column: x => x.DeviceRowId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceID_Environment",
                table: "Devices",
                columns: new[] { "DeviceID", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_DeviceRowId_FiscalDayNo_ReceiptCounter",
                table: "Receipts",
                columns: new[] { "DeviceRowId", "FiscalDayNo", "ReceiptCounter" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_DeviceRowId_FormKey",
                table: "Receipts",
                columns: new[] { "DeviceRowId", "FormKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Receipts");

            migrationBuilder.DropTable(
                name: "Devices");
        }
    }
}
