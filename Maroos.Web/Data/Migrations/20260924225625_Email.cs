using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maroos.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Email : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AuctionEmails",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OutgoingEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ToAddress = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TextBody = table.Column<string>(type: "TEXT", nullable: true),
                    HtmlBody = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    AuctionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutgoingEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutgoingEmails_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingEmails_SentAtUtc_NextAttemptAtUtc",
                table: "OutgoingEmails",
                columns: new[] { "SentAtUtc", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingEmails_UserId",
                table: "OutgoingEmails",
                column: "UserId");

            // Accounts from before email confirmation keep working, and start with auction emails switched on.
            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"EmailConfirmed\" = 1, \"AuctionEmails\" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutgoingEmails");

            migrationBuilder.DropColumn(
                name: "AuctionEmails",
                table: "AspNetUsers");
        }
    }
}
