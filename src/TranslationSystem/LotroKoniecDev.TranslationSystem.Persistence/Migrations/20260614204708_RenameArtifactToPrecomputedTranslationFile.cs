using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameArtifactToPrecomputedTranslationFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No schema change: this migration only re-syncs the model snapshot after the
            // TranslationArtifact aggregate was renamed to the PrecomputedTranslationFile projection
            // (ADR-0003). The physical table ("TranslationArtifacts") and its columns are unchanged.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
