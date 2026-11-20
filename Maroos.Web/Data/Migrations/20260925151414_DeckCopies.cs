using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maroos.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeckCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CopiedFromDeckId",
                table: "Decks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Decks_CopiedFromDeckId",
                table: "Decks",
                column: "CopiedFromDeckId");

            migrationBuilder.AddForeignKey(
                name: "FK_Decks_Decks_CopiedFromDeckId",
                table: "Decks",
                column: "CopiedFromDeckId",
                principalTable: "Decks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Decks_Decks_CopiedFromDeckId",
                table: "Decks");

            migrationBuilder.DropIndex(
                name: "IX_Decks_CopiedFromDeckId",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "CopiedFromDeckId",
                table: "Decks");
        }
    }
}
