using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.TranslationSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslatorAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Translators",
                schema: "translation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProvisionedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Translators", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Translations_ApprovedById",
                schema: "translation",
                table: "Translations",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_Translations_SubmittedById",
                schema: "translation",
                table: "Translations",
                column: "SubmittedById");

            migrationBuilder.CreateIndex(
                name: "IX_Translators_IdentityId",
                schema: "translation",
                table: "Translators",
                column: "IdentityId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Translations_Translators_ApprovedById",
                schema: "translation",
                table: "Translations",
                column: "ApprovedById",
                principalSchema: "translation",
                principalTable: "Translators",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Translations_Translators_SubmittedById",
                schema: "translation",
                table: "Translations",
                column: "SubmittedById",
                principalSchema: "translation",
                principalTable: "Translators",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Translations_Translators_ApprovedById",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropForeignKey(
                name: "FK_Translations_Translators_SubmittedById",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropTable(
                name: "Translators",
                schema: "translation");

            migrationBuilder.DropIndex(
                name: "IX_Translations_ApprovedById",
                schema: "translation",
                table: "Translations");

            migrationBuilder.DropIndex(
                name: "IX_Translations_SubmittedById",
                schema: "translation",
                table: "Translations");
        }
    }
}
