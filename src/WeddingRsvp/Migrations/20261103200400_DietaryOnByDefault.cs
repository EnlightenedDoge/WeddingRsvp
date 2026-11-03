using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeddingRsvp.Migrations
{
    /// <inheritdoc />
    public partial class DietaryOnByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The dietary question is now on by default (the couple can still turn it off): switch it on for existing couples too.
            migrationBuilder.Sql("UPDATE EventSettings SET AskDietary = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
