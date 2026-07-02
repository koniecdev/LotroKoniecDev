using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslationSearchAndStatusIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "IX_Translations_SourceText",
                schema: "translation",
                table: "Translations",
                column: "SourceText")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Translations_Status",
                schema: "translation",
                table: "Translations",
                column: "Status",
                filter: "\"RemovedInVersion\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Translations_TranslatedText",
                schema: "translation",
                table: "Translations",
                column: "TranslatedText")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Translations_SourceText",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropIndex(
                name: "IX_Translations_Status",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropIndex(
                name: "IX_Translations_TranslatedText",
                schema: "translation",
                table: "Translations");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
