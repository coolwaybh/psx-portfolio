using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCashDividendSymbolLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkedEntryId",
                table: "CashEntries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Symbol",
                table: "CashEntries",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashEntries_LinkedEntryId",
                table: "CashEntries",
                column: "LinkedEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashEntries_CashEntries_LinkedEntryId",
                table: "CashEntries",
                column: "LinkedEntryId",
                principalTable: "CashEntries",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashEntries_CashEntries_LinkedEntryId",
                table: "CashEntries");

            migrationBuilder.DropIndex(
                name: "IX_CashEntries_LinkedEntryId",
                table: "CashEntries");

            migrationBuilder.DropColumn(
                name: "LinkedEntryId",
                table: "CashEntries");

            migrationBuilder.DropColumn(
                name: "Symbol",
                table: "CashEntries");
        }
    }
}
