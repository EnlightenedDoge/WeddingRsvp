using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeddingRsvp.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviewToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviewToken",
                table: "EventSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_EventSettings_PreviewToken",
                table: "EventSettings",
                column: "PreviewToken",
                unique: true,
                filter: "\"PreviewToken\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventSettings_PreviewToken",
                table: "EventSettings");

            migrationBuilder.DropColumn(
                name: "PreviewToken",
                table: "EventSettings");
        }
    }
}
