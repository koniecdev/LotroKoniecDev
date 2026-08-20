using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.AuthSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailChangeRevertFieldsToUsers : Migration
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

            migrationBuilder.AddColumn<string>(
                name: "EmailChangeRevertTo",
                schema: "authsystem",
                table: "Users",
                type: "character varying(250)",
                maxLength: 250,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailChangeRevertStamp",
                schema: "authsystem",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailChangeRevertTo",
                schema: "authsystem",
                table: "Users");
        }
    }
}
