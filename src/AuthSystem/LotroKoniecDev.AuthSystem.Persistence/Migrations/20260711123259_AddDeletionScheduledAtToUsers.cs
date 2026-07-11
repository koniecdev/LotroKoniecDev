using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.AuthSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletionScheduledAtToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletionScheduledAt",
                schema: "authsystem",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_DeletionScheduledAt",
                schema: "authsystem",
                table: "Users",
                column: "DeletionScheduledAt",
                filter: "\"DeletionScheduledAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_DeletionScheduledAt",
                schema: "authsystem",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeletionScheduledAt",
                schema: "authsystem",
                table: "Users");
        }
    }
}
