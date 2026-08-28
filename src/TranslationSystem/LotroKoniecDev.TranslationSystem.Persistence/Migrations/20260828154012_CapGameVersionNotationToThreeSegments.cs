using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CapGameVersionNotationToThreeSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MIGRATION-SAFETY: acknowledged - #728 caps a game version at three segments, so the
            // longest value the domain can now produce is "123.456.789" (11 characters), and the
            // column only follows that cap. The N-1 revision reads every value the narrowed column
            // can still hold, and the only inputs it accepts that the column would now reject are
            // 12-character strings - typos the new code refuses anyway. Registered versions come
            // from the forum, where notation is short ("49.4", "47.1.1"), so no such row is
            // expected; if one exists the ALTER fails at the deploy gate before any traffic moves
            // (ADR-0023), so it cannot truncate data.
            migrationBuilder.AlterColumn<string>(
                name: "LotroNotationVersion",
                schema: "translation",
                table: "GameVersions",
                type: "character varying(11)",
                maxLength: 11,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "LotroNotationVersion",
                schema: "translation",
                table: "GameVersions",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(11)",
                oldMaxLength: 11);
        }
    }
}
