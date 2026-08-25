using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Psx.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIsAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Seed the one admin account - the app owner's own login, not a hardcoded
            // id, since usernames are the stable/legible identifier here. No endpoint
            // ever promotes another user to admin; this is the only place it's set.
            migrationBuilder.Sql("UPDATE [Users] SET [IsAdmin] = 1 WHERE [Username] = 'sarfraz';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Users");
        }
    }
}
