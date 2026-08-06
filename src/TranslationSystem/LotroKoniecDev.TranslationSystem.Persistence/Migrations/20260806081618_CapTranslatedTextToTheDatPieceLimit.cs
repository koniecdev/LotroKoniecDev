using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <summary>
    /// Database backstop for #598 / ADR-0043, step 1 of 2: declares the constraint without scanning.
    /// <see cref="ValidateTranslatedTextLengthCap"/> validates it in a separate transaction.
    /// </summary>
    /// <remarks>
    /// MIGRATION-SAFETY: acknowledged — this tightens a constraint over existing data, so it ships as
    /// the two-step PostgreSQL form rather than the DDL EF scaffolds, split across two migrations.
    ///
    /// Why not HasMaxLength: varchar(32767) would narrow the column type, and narrowing text ->
    /// varchar rewrites the whole table and rebuilds its trigram GIN index while holding ACCESS
    /// EXCLUSIVE. Translations carries the full ~792.5k-row corpus, so that lock is a real
    /// deploy-window outage — precisely what ADR-0023 forbids, since the previous revision keeps
    /// serving throughout. ADD CONSTRAINT ... NOT VALID declares the rule without reading a single
    /// row, so this migration's ACCESS EXCLUSIVE lasts only for the catalog write.
    ///
    /// Why the split: PostgreSQL holds every lock until its transaction commits, and EF applies each
    /// migration in one transaction. Putting the VALIDATE in the same file would therefore hold this
    /// ACCESS EXCLUSIVE for the whole scan — the exact lock profile the two-step form exists to
    /// avoid. Two migrations are two transactions, so the lock is released here and reacquired at
    /// the weaker SHARE UPDATE EXCLUSIVE level by the next one.
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
