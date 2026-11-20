using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maroos.Web.Data.Migrations.Postgres
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
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OutgoingEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ToAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: true),
                    HtmlBody = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
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
            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"EmailConfirmed\" = TRUE, \"AuctionEmails\" = TRUE;");
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
