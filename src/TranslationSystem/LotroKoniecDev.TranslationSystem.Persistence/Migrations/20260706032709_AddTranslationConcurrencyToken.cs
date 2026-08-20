using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslationConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This adds no SQL by design (AUDIT-EF-01). xmin is a PostgreSQL system column that every
            // row already has, so Npgsql writes nothing for this AddColumn. The migration only updates
            // the model snapshot, which is what turns the concurrency token on. It is N-1 safe
            // (ADR-0023) because nothing in the database changes and the previous app revision keeps
            // serving.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "translation",
                table: "Translations",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "translation",
                table: "Translations");
        }
    }
}
