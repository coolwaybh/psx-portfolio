using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDividendTaxFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DividendTaxRatePct",
                table: "UserSettings",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 15m);

            migrationBuilder.AddColumn<decimal>(
                name: "GrossAmount",
                table: "CashEntries",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRatePct",
                table: "CashEntries",
                type: "decimal(5,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DividendTaxRatePct",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "GrossAmount",
                table: "CashEntries");

            migrationBuilder.DropColumn(
                name: "TaxRatePct",
                table: "CashEntries");
        }
    }
}
