using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.AuthSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailChangeRevertReservationToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailChangeRevertArmedAt",
                schema: "authsystem",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmailChangeRevertTo",
                schema: "authsystem",
                table: "Users",
                type: "character varying(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmailChangeRevertTo",
                schema: "authsystem",
                table: "Users",
                column: "NormalizedEmailChangeRevertTo",
                filter: "\"NormalizedEmailChangeRevertTo\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedEmailChangeRevertTo",
                schema: "authsystem",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailChangeRevertArmedAt",
                schema: "authsystem",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NormalizedEmailChangeRevertTo",
                schema: "authsystem",
                table: "Users");
        }
    }
}
