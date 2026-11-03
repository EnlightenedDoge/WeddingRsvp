using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeddingRsvp.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AddedByGuestId",
                table: "Guests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroupLinkId",
                table: "Guests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsChild",
                table: "Guests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GroupLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CoupleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MaxGuests = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpiresAtLocal = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsOff = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupLinks_Couples_CoupleId",
                        column: x => x.CoupleId,
                        principalTable: "Couples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Guests_AddedByGuestId",
                table: "Guests",
                column: "AddedByGuestId");

            migrationBuilder.CreateIndex(
                name: "IX_Guests_GroupLinkId",
                table: "Guests",
                column: "GroupLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupLinks_Code",
                table: "GroupLinks",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupLinks_CoupleId",
                table: "GroupLinks",
                column: "CoupleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupLinks");

            migrationBuilder.DropIndex(
                name: "IX_Guests_AddedByGuestId",
                table: "Guests");

            migrationBuilder.DropIndex(
                name: "IX_Guests_GroupLinkId",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "AddedByGuestId",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "GroupLinkId",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "IsChild",
                table: "Guests");
        }
    }
}
