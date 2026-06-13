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
            migrationBuilder.RenameColumn(
                name: "Version",
                schema: "translation",
                table: "GameVersions",
                newName: "LotroNotationVersion");

            migrationBuilder.RenameIndex(
                name: "IX_GameVersions_Version",
                schema: "translation",
                table: "GameVersions",
                newName: "IX_GameVersions_LotroNotationVersion");

            migrationBuilder.AlterColumn<string>(
                name: "LotroNotationVersion",
                schema: "translation",
                table: "GameVersions",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "LotroNotationVersion",
                schema: "translation",
                table: "GameVersions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12);

            migrationBuilder.RenameIndex(
                name: "IX_GameVersions_LotroNotationVersion",
                schema: "translation",
                table: "GameVersions",
                newName: "IX_GameVersions_Version");

            migrationBuilder.RenameColumn(
                name: "LotroNotationVersion",
                schema: "translation",
                table: "GameVersions",
                newName: "Version");
        }
    }
}
