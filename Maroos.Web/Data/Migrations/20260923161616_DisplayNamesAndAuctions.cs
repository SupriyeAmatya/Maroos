using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maroos.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DisplayNamesAndAuctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Auctions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SellerId = table.Column<string>(type: "TEXT", nullable: false),
                    ScryfallId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CardName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SetCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    SetName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CollectorNumber = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Condition = table.Column<int>(type: "INTEGER", nullable: false),
                    IsFoil = table.Column<bool>(type: "INTEGER", nullable: false),
                    PhotoPath = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StartCents = table.Column<long>(type: "INTEGER", nullable: false),
                    IncrementCents = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    WinnerId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Auctions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Auctions_AspNetUsers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Auctions_AspNetUsers_WinnerId",
                        column: x => x.WinnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_SellerId",
                table: "Auctions",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_Status_EndsAtUtc",
                table: "Auctions",
                columns: new[] { "Status", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_WinnerId",
                table: "Auctions",
                column: "WinnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Auctions");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "AspNetUsers");
        }
    }
}
