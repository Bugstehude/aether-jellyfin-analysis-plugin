using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent wie die Basis-Migration: eine Datenbank, die per
            // EnsureCreated aus dem AKTUELLEN Modell entstanden ist, enthält
            // diese Tabelle bereits — ein einfaches CREATE TABLE scheitert dort
            // mit "table already exists" und lässt die Migration mitten im Lauf
            // stehen. Ein bestehender Test deckt genau diesen Übernahme-Pfad ab
            // und hat den Fehler sofort gemeldet.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "voice_recordings" (
                    "LineId" TEXT NOT NULL CONSTRAINT "PK_voice_recordings" PRIMARY KEY,
                    "ContentType" TEXT NOT NULL,
                    "Content" BLOB NOT NULL,
                    "ContentLength" INTEGER NOT NULL,
                    "UpdatedAtUnixTimeMilliseconds" INTEGER NOT NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "voice_recordings";""");
        }
    }
}
