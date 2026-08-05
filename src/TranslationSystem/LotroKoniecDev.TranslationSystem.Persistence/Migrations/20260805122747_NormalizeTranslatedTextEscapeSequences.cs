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
    /// MIGRATION-SAFETY: acknowledged — this rewrites existing data and cannot be undone by Down().
    /// It is behavior-PRESERVING rather than destructive: before ADR-0039 the serializer emitted
    /// TranslatedText verbatim and the patcher unescaped it unconditionally, so a stored "\n" always
    /// reached the DAT as a line feed. Escaping on write (the fix) would otherwise turn that same row
    /// into a literal backslash-n in game. Applying the OLD reader to the column reproduces exactly
    /// what the game already shows, and loses nothing that could ever have been expressed — the old
    /// pipeline had no way to put a literal backslash-n into the DAT.
    ///
    /// Source columns are deliberately NOT converted: their stored form is genuinely ambiguous (the
    /// old escape was not injective), and the import pipeline repairs them for real on the next
    /// re-export + re-import. See ADR-0039, "The migration cost".
    ///
    /// N-1 note (ADR-0023): the previous revision serializes a real newline verbatim, so during a
    /// deploy window a converted row can drop out of the artifact — the pre-existing #596 symptom,
    /// transient, on a regenerable projection that the next write rebuilds.
    /// </remarks>
    public partial class NormalizeTranslatedTextEscapeSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Same order as the old TranslationFileParser.UnescapeContent: \r first, then \n.
            //
            // Two literal traps, both load-bearing:
            //   * strpos, not LIKE — in a LIKE pattern PostgreSQL treats the backslash as the escape
            //     character, so '%\r%' would silently mean "contains r" and rewrite every row.
            //   * E'\\r', not '\r' — a plain literal means backslash+r only while
            //     standard_conforming_strings is on. With it off, '\r' parses as a carriage RETURN,
            //     the WHERE stops matching the rows that need converting, and the migration records
            //     itself as applied having done nothing. The E'' form is backslash+r under both.
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
            // Deliberately empty: migrations are forward-only (ADR-0023), and re-escaping would be a
            // guess — a row may legitimately hold a real newline that was never a "\n" sequence.
        }
    }
}
