using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceQualityProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent wie die vorherigen Migrationen (AddVoiceRecordings,
            // AddManualPresets): eine Datenbank, die per EnsureCreated aus dem
            // AKTUELLEN Modell entstanden ist, enthält diese Tabelle bereits — ein
            // einfaches CREATE TABLE scheitert dort mit "table already exists" und
            // lässt die Migration mitten im Lauf stehen.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "device_quality_profiles" (
                    "Client" TEXT NOT NULL,
                    "Ladder" TEXT NOT NULL,
                    "DeviceIdentifier" TEXT NOT NULL,
                    "DeviceDescription" TEXT NOT NULL,
                    "Mode" TEXT NOT NULL,
                    "FinishedAt" TEXT NOT NULL,
                    "EntryCount" INTEGER NOT NULL,
                    "FallbackStartStepIndex" INTEGER NULL,
                    "DocumentJson" TEXT NOT NULL,
                    "StoredAtUnixTimeMilliseconds" INTEGER NOT NULL,
                    CONSTRAINT "PK_device_quality_profiles" PRIMARY KEY ("Client", "Ladder", "DeviceIdentifier")
                );
                CREATE INDEX IF NOT EXISTS "IX_device_quality_profiles_StoredAtUnixTimeMilliseconds"
                    ON "device_quality_profiles" ("StoredAtUnixTimeMilliseconds");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "device_quality_profiles";""");
        }
    }
}
