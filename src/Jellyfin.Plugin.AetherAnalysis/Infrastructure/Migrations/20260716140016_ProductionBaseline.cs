using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProductionBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent DDL adopts databases created by the private 0.1 development build.
            // Later migrations are ordinary generated EF diffs from the checked-in snapshot.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "analysis_records" (
                    "ItemId" TEXT NOT NULL,
                    "MediaSourceId" TEXT NOT NULL,
                    "AlgorithmId" TEXT NOT NULL,
                    "AlgorithmVersion" TEXT NOT NULL,
                    "MediaFingerprint" TEXT NOT NULL,
                    "FingerprintQuality" TEXT NOT NULL,
                    "Etag" TEXT NOT NULL,
                    "CompressedDocument" BLOB NOT NULL,
                    "UncompressedBytes" INTEGER NOT NULL,
                    "FrameCount" INTEGER NOT NULL,
                    "SourceIntervalMs" INTEGER NOT NULL,
                    "CreatedAtUnixTimeMilliseconds" INTEGER NOT NULL,
                    "StoredAtUnixTimeMilliseconds" INTEGER NOT NULL,
                    "LastAccessedAtUnixTimeMilliseconds" INTEGER NOT NULL,
                    CONSTRAINT "PK_analysis_records" PRIMARY KEY ("ItemId", "MediaSourceId", "AlgorithmId", "AlgorithmVersion")
                );
                CREATE INDEX IF NOT EXISTS "IX_analysis_records_LastAccessedAtUnixTimeMilliseconds"
                    ON "analysis_records" ("LastAccessedAtUnixTimeMilliseconds");
                CREATE INDEX IF NOT EXISTS "IX_analysis_records_StoredAtUnixTimeMilliseconds"
                    ON "analysis_records" ("StoredAtUnixTimeMilliseconds");
                CREATE TABLE IF NOT EXISTS "analysis_maintenance_state" (
                    "Id" INTEGER NOT NULL,
                    "LastCompletedAtUnixTimeMilliseconds" INTEGER NULL,
                    "LastReason" TEXT NOT NULL,
                    "LastRetentionDeletedRecords" INTEGER NOT NULL,
                    "LastCapacityDeletedRecords" INTEGER NOT NULL,
                    "LastDeletedBytes" INTEGER NOT NULL,
                    CONSTRAINT "PK_analysis_maintenance_state" PRIMARY KEY ("Id")
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analysis_maintenance_state");

            migrationBuilder.DropTable(
                name: "analysis_records");
        }
    }
}
