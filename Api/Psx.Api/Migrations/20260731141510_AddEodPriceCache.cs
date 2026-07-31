using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEodPriceCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EodPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Close = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EodPrices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EodPrices_Symbol_Date",
                table: "EodPrices",
                columns: new[] { "Symbol", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EodPrices");
        }
    }
}
