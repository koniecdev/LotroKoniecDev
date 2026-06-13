using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslationAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Translations",
                schema: "translation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    GossipId = table.Column<long>(type: "bigint", nullable: false),
                    TranslatedText = table.Column<string>(type: "text", nullable: true),
                    PreviousSourceText = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IntroducedInVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    LastSourceChangeInVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    RemovedInVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArgsId = table.Column<string>(type: "text", nullable: true),
                    ArgsOrder = table.Column<string>(type: "text", nullable: true),
                    SourceText = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Translations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Translations_FileId_GossipId",
                schema: "translation",
                table: "Translations",
                columns: new[] { "FileId", "GossipId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Translations",
                schema: "translation");
        }
    }
}
