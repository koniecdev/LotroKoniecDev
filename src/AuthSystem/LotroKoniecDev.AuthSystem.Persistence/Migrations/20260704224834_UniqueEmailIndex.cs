using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.AuthSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UniqueEmailIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                schema: "authsystem",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "authsystem",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                schema: "authsystem",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "authsystem",
                table: "Users",
                column: "NormalizedEmail");
        }
    }
}
