using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefactorGameVersionToOwnsOne : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GameVersions_Version",
                schema: "translation",
                table: "GameVersions");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "translation",
                table: "GameVersions");

            migrationBuilder.AddColumn<string>(
                name: "LotroNotationVersion",
                schema: "translation",
                table: "GameVersions",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_GameVersions_LotroNotationVersion",
                schema: "translation",
                table: "GameVersions",
                column: "LotroNotationVersion",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GameVersions_LotroNotationVersion",
                schema: "translation",
                table: "GameVersions");

            migrationBuilder.DropColumn(
                name: "LotroNotationVersion",
                schema: "translation",
                table: "GameVersions");

            migrationBuilder.AddColumn<string>(
                name: "Version",
                schema: "translation",
                table: "GameVersions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_GameVersions_Version",
                schema: "translation",
                table: "GameVersions",
                column: "Version",
                unique: true);
        }
    }
}
