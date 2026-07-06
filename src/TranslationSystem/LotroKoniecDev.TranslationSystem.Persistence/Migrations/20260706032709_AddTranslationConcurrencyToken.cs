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
            // No-op DDL by design (AUDIT-EF-01): xmin is a PostgreSQL system column that already
            // exists on every row, so Npgsql emits zero SQL for this AddColumn — the migration only
            // syncs the model snapshot so the optimistic-concurrency token engages. Trivially N-1-safe
            // (ADR-0023): nothing physical changes, so the previous app revision keeps serving.
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
