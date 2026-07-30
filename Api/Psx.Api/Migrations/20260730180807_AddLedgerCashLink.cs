using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerCashLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LedgerEntryId",
                table: "CashEntries",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashEntries_LedgerEntryId",
                table: "CashEntries",
                column: "LedgerEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashEntries_LedgerEntries_LedgerEntryId",
                table: "CashEntries",
                column: "LedgerEntryId",
                principalTable: "LedgerEntries",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashEntries_LedgerEntries_LedgerEntryId",
                table: "CashEntries");

            migrationBuilder.DropIndex(
                name: "IX_CashEntries_LedgerEntryId",
                table: "CashEntries");

            migrationBuilder.DropColumn(
                name: "LedgerEntryId",
                table: "CashEntries");
        }
    }
}
