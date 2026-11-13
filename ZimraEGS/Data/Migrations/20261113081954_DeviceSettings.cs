using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZimraEGS.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivationKeyHash",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultMoneyType",
                table: "Devices",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivationKeyHash",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "DefaultMoneyType",
                table: "Devices");
        }
    }
}
