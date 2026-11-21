using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <summary>
    /// Data-only cleaning for #1476: removes the trailing line breaks that a translator's Enter key left
    /// on saved Polish, where the English does not end with one. It is the same rule as
    /// <c>TranslationSource.RemoveTrailingLineBreaksFrom</c> in the domain, so the text the translator
    /// meant does not change.
    /// </summary>
    /// <remarks>
    /// MIGRATION-SAFETY: acknowledged. This rewrites existing data and Down() cannot undo it. Only the
    /// text changes: status, author, approver and dates stay, because no trigger or interceptor touches
    /// them.
    ///
    /// A row whose Polish is only whitespace is left alone. Removing its breaks would leave a blank
    /// text, which the domain never stores, so a person fixes it in the editor.
    ///
    /// N-1 note (ADR-0023): the previous revision still saves trailing breaks until traffic moves to
    /// the new one. Running this UPDATE again by hand after the deploy cleans those rows; it is safe to
    /// run twice. Raw SQL schedules no rebuild of the player file, so the file shows a cleaned
    /// approved row only after the next rebuild (an approve, an edit of an approved row or an import).
    /// </remarks>
    public partial class RemoveTrailingLineBreaksFromTranslatedText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CHR() and a regex with no backslash mean the same whatever standard_conforming_strings is
            // set to (see 20260805122747). rtrim takes a set of characters, so it removes any run of CR
            // and LF at the end.
            migrationBuilder.Sql(
                """
                UPDATE translation."Translations"
                SET "TranslatedText" = rtrim("TranslatedText", CHR(13) || CHR(10))
                WHERE right("TranslatedText", 1) IN (CHR(13), CHR(10))
                  AND right("SourceText", 1) NOT IN (CHR(13), CHR(10))
                  AND "TranslatedText" !~ '^[[:space:]]*$';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Empty on purpose. Migrations only go forward (ADR-0023), and the removed breaks carried
            // nothing the translator meant to keep.
        }
    }
}
