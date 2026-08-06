using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <summary>
    /// Database backstop for #598 / ADR-0043, step 2 of 2: validates the constraint
    /// <see cref="CapTranslatedTextToTheDatPieceLimit"/> declared NOT VALID, in its own transaction.
    /// </summary>
    /// <remarks>
    /// MIGRATION-SAFETY: acknowledged — deliberate contract step; the declaring step shipped in the
    /// migration immediately before this one, in the same release.
    ///
    /// VALIDATE CONSTRAINT takes SHARE UPDATE EXCLUSIVE, which lets reads and writes through for the
    /// scan — the whole reason this is a separate migration rather than a second statement in the
    /// previous one (see its remarks). No table rewrite and no index rebuild either way.
    ///
    /// Failure is safe by construction: the NOT VALID constraint is already committed by the previous
    /// migration, so it keeps binding every new write even if this scan fails on legacy data, and
    /// re-running the migrator retries only this step. Offending rows, if any, are found with
    ///     SELECT "Id" FROM translation."Translations" WHERE length("TranslatedText") > 32767;
    /// Measured on the shipped corpus (792,500 rows) there are none: longest English source is 5,959
    /// characters and the average is 66.
    ///
    /// The bound is length() — code points — while the DAT counts UTF-16 code units, so a text made
    /// entirely of astral-plane characters could pass here and still be refused at the API boundary.
    /// Deliberate: the exact measure belongs in C#, where string.Length already is that unit, and a
    /// backstop that over-rejected legitimate Polish would be the worse error.
    /// </remarks>
    public partial class ValidateTranslatedTextLengthCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE translation."Translations"
                VALIDATE CONSTRAINT "CK_Translations_TranslatedText_MaxLength";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: migrations are forward-only (ADR-0023), and PostgreSQL offers no
            // "invalidate constraint". Dropping the constraint outright belongs to the declaring
            // migration's Down(), not here.
        }
    }
}
