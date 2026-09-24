using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMufapNavCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MufapNavs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FundName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Nav = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MufapNavs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MufapNavs_FundName",
                table: "MufapNavs",
                column: "FundName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MufapNavs");
        }
    }
}
