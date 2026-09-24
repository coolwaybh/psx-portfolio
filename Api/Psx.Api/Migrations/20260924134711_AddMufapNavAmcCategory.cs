using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMufapNavAmcCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Amc",
                table: "MufapNavs",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "MufapNavs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Amc",
                table: "MufapNavs");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "MufapNavs");
        }
    }
}
