using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <summary>
    /// Data-only backfill for ADR-0039 / #596: converts the two-character <c>\r</c>/<c>\n</c>
    /// sequences translators typed under the old pipeline into the real control characters the
    /// column now holds.
    /// </summary>
    /// <remarks>
    /// MIGRATION-SAFETY: acknowledged. This rewrites existing data and Down() cannot undo it.
    /// It keeps behaviour the same rather than changing it. Before ADR-0039 the serializer wrote
    /// TranslatedText as it was and the patcher always unescaped it, so a stored "\n" always reached
    /// the DAT as a line feed. With the fix, which escapes on write, that same row would show a
    /// literal backslash and n in the game. Reading the column the old way reproduces exactly what
    /// players already see, and nothing is lost, because the old pipeline could not put a literal
    /// backslash and n into the DAT at all.
    ///
    /// The source columns are not converted, on purpose. Their stored form really is ambiguous,
    /// because the old escape could produce the same text from two different inputs, and the import
    /// pipeline repairs them properly on the next export and import. See ADR-0039, "The migration
    /// cost".
    ///
    /// N-1 note (ADR-0023): the previous revision writes a real newline as it is, so during a deploy
    /// a converted row can drop out of the artifact. That is the existing #596 symptom, it is
    /// temporary, and it happens on a projection the next write rebuilds.
    /// </remarks>
    public partial class NormalizeTranslatedTextEscapeSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Same order as the old TranslationFileParser.UnescapeContent: \r first, then \n.
            //
            // Two literal traps, both load-bearing:
            //   * strpos and not LIKE. In a LIKE pattern PostgreSQL reads the backslash as an escape
            //     character, so '%\r%' would quietly mean "contains r" and rewrite every row.
            //   * E'\\r' and not '\r'. A plain literal means backslash and r only while
            //     standard_conforming_strings is on. With it off, '\r' is a carriage return, the WHERE
            //     stops matching the rows that need converting, and the migration marks itself as
            //     applied having done nothing. The E'' form means backslash and r either way.
            migrationBuilder.Sql(
                """
                UPDATE translation."Translations"
                SET "TranslatedText" = replace(replace("TranslatedText", E'\\r', CHR(13)), E'\\n', CHR(10))
                WHERE strpos("TranslatedText", E'\\r') > 0 OR strpos("TranslatedText", E'\\n') > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Empty on purpose. Migrations only go forward (ADR-0023), and escaping the text again
            // would be guesswork: a row may hold a real newline that was never a "\n" sequence.
        }
    }
}
