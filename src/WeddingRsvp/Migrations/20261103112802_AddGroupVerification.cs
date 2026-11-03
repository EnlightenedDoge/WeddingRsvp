using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeddingRsvp.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VerifyMode",
                table: "GroupLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PhoneVerifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CoupleId = table.Column<int>(type: "INTEGER", nullable: false),
                    PhoneE164 = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Salt = table.Column<string>(type: "TEXT", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhoneVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhoneVerifications_Couples_CoupleId",
                        column: x => x.CoupleId,
                        principalTable: "Couples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhoneVerifications_CoupleId_PhoneE164",
                table: "PhoneVerifications",
                columns: new[] { "CoupleId", "PhoneE164" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhoneVerifications");

            migrationBuilder.DropColumn(
                name: "VerifyMode",
                table: "GroupLinks");
        }
    }
}
