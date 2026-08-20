using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <summary>
    /// Database backstop for #598 / ADR-0043, step 1 of 2: declares the constraint without scanning.
    /// <see cref="ValidateTranslatedTextLengthCap"/> validates it in a separate transaction.
    /// </summary>
    /// <remarks>
    /// MIGRATION-SAFETY: acknowledged. This tightens a rule over data that already exists, so it uses
    /// the two-step PostgreSQL form instead of the DDL EF would scaffold, split over two migrations.
    ///
    /// Why not HasMaxLength: varchar(32767) would change the column type, and changing text to varchar
    /// rewrites the whole table and rebuilds its trigram GIN index while holding ACCESS EXCLUSIVE.
    /// Translations holds the full corpus of about 792,500 rows, so that lock is a real outage during
    /// the deploy, and ADR-0023 forbids exactly that, because the previous revision keeps serving the
    /// whole time. ADD CONSTRAINT ... NOT VALID declares the rule without reading a single row, so the
    /// ACCESS EXCLUSIVE lock here lasts only for the catalog write.
    ///
    /// Why two files: PostgreSQL keeps every lock until its transaction commits, and EF applies each
    /// migration in one transaction. Putting the VALIDATE in the same file would hold this ACCESS
    /// EXCLUSIVE lock for the whole scan, which is exactly what the two-step form avoids. Two
    /// migrations are two transactions, so the lock is released here and taken again at the weaker
    /// SHARE UPDATE EXCLUSIVE level by the next one.
    ///
    /// N-1 (ADR-0023): the previous revision has no length rule of its own, but the only write it
    /// loses is one that the current revision would already reject and that the patcher could not
    /// apply. Measured on the shipped corpus (792,500 rows): longest English source 5,959 characters,
    /// average 66, zero rows above 32767.
    /// </remarks>
    public partial class CapTranslatedTextToTheDatPieceLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE translation."Translations"
                ADD CONSTRAINT "CK_Translations_TranslatedText_MaxLength"
                CHECK ("TranslatedText" IS NULL OR length("TranslatedText") <= 32767) NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Translations_TranslatedText_MaxLength",
                schema: "translation",
                table: "Translations");
        }
    }
}
