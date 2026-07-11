using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.AuthSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTermsOfServiceAcceptanceToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TermsOfServiceAccepted",
                schema: "authsystem",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TermsOfServiceAcceptedDate",
                schema: "authsystem",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TermsOfServiceAccepted",
                schema: "authsystem",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TermsOfServiceAcceptedDate",
                schema: "authsystem",
                table: "Users");
        }
    }
}
