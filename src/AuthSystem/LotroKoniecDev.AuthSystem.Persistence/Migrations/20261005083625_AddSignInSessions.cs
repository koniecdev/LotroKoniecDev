using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LotroKoniecDev.AuthSystem.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSignInSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignInSessions",
                schema: "authsystem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtectedTicket = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignInSessions_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "authsystem",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignInSessions_UserId",
                schema: "authsystem",
                table: "SignInSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignInSessions",
                schema: "authsystem");
        }
    }
}
