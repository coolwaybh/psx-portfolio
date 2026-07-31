using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStockSplitSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "LedgerEntries",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(4)",
                oldMaxLength: 4);

            migrationBuilder.AddColumn<decimal>(
                name: "SplitRatioFrom",
                table: "LedgerEntries",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SplitRatioTo",
                table: "LedgerEntries",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SplitRatioFrom",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "SplitRatioTo",
                table: "LedgerEntries");

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "LedgerEntries",
                type: "nvarchar(4)",
                maxLength: 4,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);
        }
    }
}
