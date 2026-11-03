using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeddingRsvp.Migrations
{
    /// <inheritdoc />
    public partial class AddDietary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DietGlutenFree",
                table: "Guests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DietNote",
                table: "Guests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DietVegan",
                table: "Guests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DietVegetarian",
                table: "Guests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AskDietary",
                table: "EventSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DietGlutenFree",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DietNote",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DietVegan",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "DietVegetarian",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "AskDietary",
                table: "EventSettings");
        }
    }
}
