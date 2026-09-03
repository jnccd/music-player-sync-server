using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using MusicPlayerSyncInterface;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerSyncServer.Database;

/// <summary>
/// Heals duplicated UpvotedSong rows that slipped into the database before duplicate uploads were
/// rejected (a song is identified by user + file name + album/artist tags, but clients that registered
/// the same file separately used their own SongIds, so both copies ended up in the same account).
/// Runs on every server start (see Program.cs) and is idempotent: once the data is clean it is a no-op.
/// Rows are only merged when they are provably the same song (exact same user, file name and stored
/// album/artist tags). Duplicates that differ in tag completeness ("" vs real tags) cannot be proven to
/// be the same song without the actual song file and are therefore NOT merged here; the clients merge
/// those after their pulls, using the song files of their library as the arbiter (see
/// MusicPlayerSyncInterface.SongFileMatching.MergeSameSongEntries).
/// </summary>
public static class UpvotedSongDeduplicator
{
    /// <summary>
    /// Merges every group of exact duplicate rows of one song into its canonical row (see
    /// SongFileMatching.ChooseCanonicalEntry: highest score wins, oldest DateAdded as tie-break, smallest
    /// SongId as last resort). The canonical row keeps its own counters and history; the history entries
    /// of the merged-away rows are removed with them. Returns how many rows were merged away.
    /// </summary>
    public static int MergeDuplicateUpvotedSongs(SongDbContext songDbContext)
    {
        UpvotedSong[] songs = songDbContext.UpvotedSongs.ToArray();
        var duplicateGroups = songs
            .GroupBy(s => new { s.UserId, s.Name, s.Artist, s.Album })
            .Where(group => group.Count() > 1)
            .ToArray();

        int mergedAway = 0;
        foreach (var group in duplicateGroups)
        {
            var (keep, remove) = SongFileMatching.MergeSameSongEntries(group);
            Guid[] removedIds = remove.Select(entry => entry.SongId).ToArray();

            // Drop the history entries of the merged-away rows with them (the kept row keeps its own
            // history). Removed explicitly so the outcome does not depend on the database cascade.
            var orphanedHistory = songDbContext.SongHistoryEntries
                .Where(h => h.SongId != null && removedIds.Contains(h.SongId.Value))
                .ToArray();
            if (orphanedHistory.Length > 0)
                songDbContext.SongHistoryEntries.RemoveRange(orphanedHistory);

            songDbContext.UpvotedSongs.RemoveRange(remove);

            Console.WriteLine($"Healed duplicate rows of \"{keep.Name}\" (artist: {keep.Artist}, album: {keep.Album}, user: {keep.UserId}): kept {keep.SongId}, merged away {remove.Length} row(s) with their history.");
            mergedAway += remove.Length;
        }

        if (mergedAway > 0)
            songDbContext.SaveChanges();

        return mergedAway;
    }

    /// <summary>
    /// Makes sure the unique index on (UserId, Name, Artist, Album) exists, so duplicate rows cannot be
    /// inserted anymore (not even by two racing clients or by older clients that upload after this server
    /// version was deployed). Call this only after the duplicates were merged away, since the index cannot
    /// be created while duplicates exist.
    /// </summary>
    public static void EnsureUniqueSongIndex(SongDbContext songDbContext)
    {
        try
        {
            // IF NOT EXISTS works on both supported providers (PostgreSQL and SQLite).
            songDbContext.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UpvotedSongs_UserId_Name_Artist_Album\" " +
                "ON \"UpvotedSongs\" (\"UserId\", \"Name\", \"Artist\", \"Album\")");
            Console.WriteLine("Ensured unique index IX_UpvotedSongs_UserId_Name_Artist_Album on UpvotedSongs.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not ensure the unique index on UpvotedSongs: {ex.Message}");
        }
    }
}
