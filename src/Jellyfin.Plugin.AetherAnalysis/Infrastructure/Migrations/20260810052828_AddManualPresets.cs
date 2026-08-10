using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddManualPresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent wie die vorherige Migration (AddVoiceRecordings): eine
            // Datenbank, die per EnsureCreated aus dem AKTUELLEN Modell entstanden
            // ist, enthält diese Tabelle bereits — ein einfaches CREATE TABLE
            // scheitert dort mit "table already exists" und lässt die Migration
            // mitten im Lauf stehen.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "manual_presets" (
                    "UserId" TEXT NOT NULL,
                    "Id" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "SnapshotJson" TEXT NOT NULL,
                    "UpdatedAtUnixTimeMilliseconds" INTEGER NOT NULL,
                    CONSTRAINT "PK_manual_presets" PRIMARY KEY ("UserId", "Id")
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "manual_presets";""");
        }
    }
}
