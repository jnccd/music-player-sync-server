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
    /// the feature (they carry the column default 0). Safe to call on every startup: the statements are
    /// idempotent, and the (relatively expensive) backfill only runs while rows with sequence 0 exist.
    /// </summary>
    public static void EnsureSequenceColumn(SongDbContext songDbContext)
    {
        bool isSqlite = songDbContext.Database.IsSqlite();

        try
        {
            songDbContext.Database.ExecuteSqlRaw(isSqlite ? SequenceColumnSqlSqlite : SequenceColumnSqlPostgres);
        }
        catch (Exception ex)
        {
            // ADD COLUMN IF NOT EXISTS does not throw when the column is already there, so reaching this
            // is an actual problem (e.g. missing permissions) rather than the normal "already exists" case.
            Console.WriteLine($"Could not ensure the history sequence column: {ex.Message}");
        }

        try
        {
            songDbContext.Database.ExecuteSqlRaw(isSqlite ? SequenceIndexSqlSqlite : SequenceIndexSqlPostgres);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not ensure the history sequence index: {ex.Message}");
        }

        try
        {
            if (!HasRowsWithoutSequence(songDbContext))
            {
                Console.WriteLine("History sequence column and index are up to date.");
                return;
            }

            int backfilled = songDbContext.Database.ExecuteSqlRaw(isSqlite ? BackfillSqlSqlite : BackfillSqlPostgres);
            Console.WriteLine($"Backfilled the history sequence of {backfilled} pre-existing history entry/entries.");
        }
        catch (Exception ex)
        {
            // Rows without a sequence (0) are still delivered by the inclusive cursor / the full pull, so
            // this is not fatal; the next startup retries (the backfill only touches sequence 0 rows).
            Console.WriteLine($"History sequence backfill failed (pre-existing entries stay at sequence 0): {ex.Message}");
        }
    }

    /// <summary>
    /// True while at least one history entry still carries the column default 0, i.e. predates the
    /// sequence feature and has not been backfilled. Uses ADO directly so no provider-specific query
    /// translation is involved.
    /// </summary>
    static bool HasRowsWithoutSequence(SongDbContext songDbContext)
    {
        var connection = songDbContext.Database.GetDbConnection();
        bool openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            songDbContext.Database.OpenConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM \"SongHistoryEntries\" WHERE \"Sequence\" = 0 LIMIT 1";
            return command.ExecuteScalar() != null;
        }
        finally
        {
            if (openedHere)
                songDbContext.Database.CloseConnection();
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
