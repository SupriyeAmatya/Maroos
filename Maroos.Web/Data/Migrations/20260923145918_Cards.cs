using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maroos.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Cards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedCards",
                columns: table => new
                {
                    ScryfallId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedCards", x => x.ScryfallId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedCards_Name",
                table: "CachedCards",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedCards");
        }
    }
}
