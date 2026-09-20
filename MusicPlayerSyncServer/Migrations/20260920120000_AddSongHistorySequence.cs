using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayerSyncServer.Database;

#nullable disable

namespace MusicPlayerSyncServer.Migrations
{
    /// <summary>
    /// Adds the server-only shadow property <c>Sequence</c> to the history entries and backfills it for
    /// existing rows: it is the cursor of the incremental history pull (see
    /// <see cref="SongHistorySequencer"/>), so clients can request only the entries they do not have yet
    /// instead of the complete history on every pull.
    /// The statements are deliberately written as idempotent raw SQL (the same ones the server ensures at
    /// startup), so applying this migration to a database whose column was already created by the startup
    /// ensure does not fail.
    /// </summary>
    [DbContext(typeof(SongDbContext))]
    [Migration("20260920120000_AddSongHistorySequence")]
    public partial class AddSongHistorySequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"SongHistoryEntries\" ADD COLUMN IF NOT EXISTS \"Sequence\" bigint NOT NULL DEFAULT 0;");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_SongHistoryEntries_UserId_Sequence\" ON \"SongHistoryEntries\" (\"UserId\", \"Sequence\");");

            // Backfill the entries that existed before this feature (they carry the column default 0),
            // numbered per user in date order; rows are only touched while they still have sequence 0,
            // so this is safe to run again.
            migrationBuilder.Sql("""
                UPDATE "SongHistoryEntries" AS h
                SET "Sequence" = t.rn
                FROM (
                    SELECT "UserId", "SongId", "Date",
                           ROW_NUMBER() OVER (PARTITION BY "UserId" ORDER BY "Date", "SongId") AS rn
                    FROM "SongHistoryEntries"
                ) AS t
                WHERE h."Sequence" = 0
                  AND h."UserId" = t."UserId" AND h."SongId" = t."SongId" AND h."Date" = t."Date";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_SongHistoryEntries_UserId_Sequence\";");
            migrationBuilder.Sql("ALTER TABLE \"SongHistoryEntries\" DROP COLUMN IF EXISTS \"Sequence\";");
        }
    }
}
