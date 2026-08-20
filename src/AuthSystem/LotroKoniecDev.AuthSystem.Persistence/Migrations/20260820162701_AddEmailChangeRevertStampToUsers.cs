using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.AuthSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailChangeRevertStampToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EmailChangeRevertStamp",
                schema: "authsystem",
                table: "Users",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailChangeRevertStamp",
                schema: "authsystem",
                table: "Users");
        }
    }
}
