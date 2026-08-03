using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCgtTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CgtRatePct",
                table: "UserSettings",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 15m);

            migrationBuilder.AddColumn<decimal>(
                name: "CgtAmount",
                table: "CashEntries",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CgtRatePct",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "CgtAmount",
                table: "CashEntries");
        }
    }
}
