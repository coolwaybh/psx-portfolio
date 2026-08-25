using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddNccplCharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "NccplChargeRatePct",
                table: "UserSettings",
                type: "decimal(7,4)",
                nullable: false,
                defaultValue: 0.007m);

            migrationBuilder.AddColumn<decimal>(
                name: "NccplCharge",
                table: "LedgerEntries",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NccplChargeRatePct",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "NccplCharge",
                table: "LedgerEntries");
        }
    }
}
