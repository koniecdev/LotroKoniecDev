using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <summary>
    /// Database backstop for #598 / ADR-0043, step 2 of 2: validates the constraint
    /// <see cref="CapTranslatedTextToTheDatPieceLimit"/> declared NOT VALID, in its own transaction.
    /// </summary>
    /// <remarks>
    /// MIGRATION-SAFETY: acknowledged. This is the deliberate second step; the first one shipped in
    /// the migration right before it, in the same release.
    ///
    /// VALIDATE CONSTRAINT takes SHARE UPDATE EXCLUSIVE, which lets reads and writes through during the
    /// scan. That is the whole reason this is its own migration and not a second statement in the
    /// previous one (see its remarks). Either way there is no table rewrite and no index rebuild.
    ///
    /// Failure is safe by construction: the NOT VALID constraint is already committed by the previous
    /// migration, so it keeps binding every new write even if this scan fails on legacy data, and
    /// re-running the migrator retries only this step. Offending rows, if any, are found with
    ///     SELECT "Id" FROM translation."Translations" WHERE length("TranslatedText") > 32767;
    /// Measured on the shipped corpus (792,500 rows) there are none: longest English source is 5,959
    /// characters and the average is 66.
    ///
    /// The limit uses length(), which counts code points, while the DAT counts UTF-16 code units. So a
    /// text made only of characters outside the basic plane could pass here and still be refused by the
    /// API. That is on purpose: the exact measure belongs in C#, where string.Length is already that
    /// unit, and a database rule that rejected valid Polish would be the worse mistake.
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
