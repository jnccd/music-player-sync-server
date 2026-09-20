using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerSyncServer.Database;

/// <summary>
/// Assigns and reads the **server-only shadow property** <c>Sequence</c> of history entries, which is the
/// cursor of the incremental history pull:
/// * Every history entry gets a per-user, monotonically increasing sequence (assigned in application
///   code, so it behaves identically on PostgreSQL and SQLite - neither provider needs an identity
///   column for a non-key property).
/// * A client stores the highest sequence it has and sends it as <c>historySince</c>; the server answers
///   with every entry of that user whose sequence is &gt;= that value (inclusive, so that entries sharing
///   a sequence - possible when two votes race - can never be skipped; clients deduplicate by their
///   primary key anyway).
/// * Entries that were inserted before this feature existed carry the column default 0 and are
///   backfilled once, ordered by date, so existing databases immediately have a meaningful cursor.
/// The column/index are created by an idempotent EF migration and additionally ensured at startup
/// (see <see cref="EnsureSequenceColumn"/>), so a server that is started without applying the migration
/// still works.
/// </summary>
public static class SongHistorySequencer
{
    const string SequenceColumnSqlPostgres =
        "ALTER TABLE \"SongHistoryEntries\" ADD COLUMN IF NOT EXISTS \"Sequence\" bigint NOT NULL DEFAULT 0;";
    const string SequenceIndexSqlPostgres =
        "CREATE INDEX IF NOT EXISTS \"IX_SongHistoryEntries_UserId_Sequence\" ON \"SongHistoryEntries\" (\"UserId\", \"Sequence\");";
    const string SequenceColumnSqlSqlite =
        "ALTER TABLE \"SongHistoryEntries\" ADD COLUMN \"Sequence\" INTEGER NOT NULL DEFAULT 0;";
    const string SequenceIndexSqlSqlite =
        "CREATE INDEX IF NOT EXISTS \"IX_SongHistoryEntries_UserId_Sequence\" ON \"SongHistoryEntries\" (\"UserId\", \"Sequence\");";

    const string BackfillSqlPostgres = """
        UPDATE "SongHistoryEntries" AS h
        SET "Sequence" = t.rn
        FROM (
            SELECT "UserId", "SongId", "Date",
                   ROW_NUMBER() OVER (PARTITION BY "UserId" ORDER BY "Date", "SongId") AS rn
            FROM "SongHistoryEntries"
        ) AS t
        WHERE h."Sequence" = 0
          AND h."UserId" = t."UserId" AND h."SongId" = t."SongId" AND h."Date" = t."Date";
        """;
    const string BackfillSqlSqlite = """
        UPDATE "SongHistoryEntries" AS h
        SET "Sequence" = t.rn
        FROM (
            SELECT "UserId", "SongId", "Date",
                   ROW_NUMBER() OVER (PARTITION BY "UserId" ORDER BY "Date", "SongId") AS rn
            FROM "SongHistoryEntries"
        ) AS t
        WHERE h."Sequence" = 0
          AND h."UserId" = t."UserId" AND h."SongId" = t."SongId" AND h."Date" = t."Date";
        """;

    /// <summary>
    /// Adds the sequence column and its index when they are missing and backfills the rows that predate
    /// the feature (default 0). Safe to call on every startup: all statements are idempotent or guarded.
    /// </summary>
    public static void EnsureSequenceColumn(SongDbContext songDbContext)
    {
        bool columnWasMissing = false;
        try
        {
            songDbContext.Database.ExecuteSqlRaw(
                songDbContext.Database.IsSqlite() ? SequenceColumnSqlSqlite : SequenceColumnSqlPostgres);
            columnWasMissing = true; // Only reached when the column did not exist (otherwise the ALTER fails)
        }
        catch (Exception ex)
        {
            // Column already exists (fresh databases get it from the migration, existing ones from a
            // previous run of this method). Nothing to do.
            _ = ex;
        }

        try
        {
            songDbContext.Database.ExecuteSqlRaw(
                songDbContext.Database.IsSqlite() ? SequenceIndexSqlSqlite : SequenceIndexSqlPostgres);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not ensure the history sequence index: {ex.Message}");
        }

        if (!columnWasMissing)
            return;

        try
        {
            int backfilled = songDbContext.Database.ExecuteSqlRaw(
                songDbContext.Database.IsSqlite() ? BackfillSqlSqlite : BackfillSqlPostgres);
            Console.WriteLine($"Added the history sequence column and backfilled {backfilled} existing history entry/entries.");
        }
        catch (Exception ex)
        {
            // Existing rows then keep sequence 0 and are delivered by inclusive cursors; a later startup
            // retries the backfill (see the "Sequence" = 0 guard in the statement).
            Console.WriteLine($"History sequence backfill failed (existing entries stay at sequence 0): {ex.Message}");
        }
    }

    /// <summary>
    /// The highest sequence the given user has (0 when the user has no history yet).
    /// </summary>
    public static long GetMaxSequence(SongDbContext songDbContext, string userId) =>
        songDbContext.SongHistoryEntries
            .Where(h => h.UserId == userId)
            .Max(h => (long?)EF.Property<long>(h, "Sequence")) ?? 0L;

    /// <summary>
    /// Assigns the next sequence of the user's stream to the given (already added/tracked) entry.
    /// </summary>
    public static void AssignSequence(SongDbContext songDbContext, SongHistoryEntry entry, long sequence) =>
        songDbContext.Entry(entry).Property("Sequence").CurrentValue = sequence;

    /// <summary>
    /// Assigns consecutive sequences to a batch of entries (e.g. the history uploaded by /sync/init),
    /// starting after the current maximum of that user's stream.
    /// </summary>
    public static void AssignSequences(SongDbContext songDbContext, string userId, IEnumerable<SongHistoryEntry> entries)
    {
        long next = GetMaxSequence(songDbContext, userId);
        foreach (SongHistoryEntry entry in entries)
            AssignSequence(songDbContext, entry, ++next);
    }
}
