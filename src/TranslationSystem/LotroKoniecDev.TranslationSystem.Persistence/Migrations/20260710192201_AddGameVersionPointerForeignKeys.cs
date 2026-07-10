using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameVersionPointerForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Translations_IntroducedInVersion",
                schema: "translation",
                table: "Translations",
                column: "IntroducedInVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Translations_LastSourceChangeInVersion",
                schema: "translation",
                table: "Translations",
                column: "LastSourceChangeInVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Translations_RemovedInVersion",
                schema: "translation",
                table: "Translations",
                column: "RemovedInVersion");

            migrationBuilder.AddForeignKey(
                name: "FK_Translations_GameVersions_IntroducedInVersion",
                schema: "translation",
                table: "Translations",
                column: "IntroducedInVersion",
                principalSchema: "translation",
                principalTable: "GameVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Translations_GameVersions_LastSourceChangeInVersion",
                schema: "translation",
                table: "Translations",
                column: "LastSourceChangeInVersion",
                principalSchema: "translation",
                principalTable: "GameVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Translations_GameVersions_RemovedInVersion",
                schema: "translation",
                table: "Translations",
                column: "RemovedInVersion",
                principalSchema: "translation",
                principalTable: "GameVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Translations_GameVersions_IntroducedInVersion",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropForeignKey(
                name: "FK_Translations_GameVersions_LastSourceChangeInVersion",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropForeignKey(
                name: "FK_Translations_GameVersions_RemovedInVersion",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropIndex(
                name: "IX_Translations_IntroducedInVersion",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropIndex(
                name: "IX_Translations_LastSourceChangeInVersion",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropIndex(
                name: "IX_Translations_RemovedInVersion",
                schema: "translation",
                table: "Translations");
        }
    }
}
