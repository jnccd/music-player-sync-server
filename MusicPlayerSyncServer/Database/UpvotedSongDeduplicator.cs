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
/// Two kinds of duplicates are merged:
/// 1. Exact duplicates: same user, file name AND stored album/artist tags. Always provable, always merged.
/// 2. Tag-completeness duplicates: same user and file name, but one row carries the album/artist of the
///    song while the other(s) are metadata-less (""), e.g. when one client registered the file WITH its
///    tags and another registered the same file without reading them. Those are merged into the tagged
///    row when ALL tagged rows of that file name share one single tag signature - a metadata-less row can
///    then not be a different same-named song, since a different song would have to carry different tags.
///    (The client-side merge additionally checks the actual song files when a library is available.)
/// </summary>
public static class UpvotedSongDeduplicator
{
    /// <summary>
    /// Merges every group of duplicate rows of one song into its canonical row (see
    /// SongFileMatching.MergeSameSongEntries: the row carrying the tags of the song wins, among exact
    /// duplicates the highest score wins, oldest DateAdded as tie-break, smallest SongId as last resort).
    /// The canonical row keeps its own counters and history; the history entries of the merged-away rows
    /// are removed with them. Returns how many rows were merged away.
    /// </summary>
    public static int MergeDuplicateUpvotedSongs(SongDbContext songDbContext)
    {
        int mergedAway = 0;

        // 1. Exact duplicates: same user, file name and stored album/artist tags.
        var duplicateGroups = songDbContext.UpvotedSongs.ToArray()
            .GroupBy(s => new { s.UserId, s.Name, s.Artist, s.Album })
            .Where(group => group.Count() > 1)
            .ToArray();
        foreach (var group in duplicateGroups)
        {
            var (keep, remove) = SongFileMatching.MergeSameSongEntries(group);
            RemoveRowsWithHistory(songDbContext, keep, remove);
            mergedAway += remove.Length;
        }

        if (mergedAway > 0)
            songDbContext.SaveChanges(); // Persist pass 1, so pass 2 only sees the surviving rows

        // 2. Tag-completeness duplicates: same user and file name, metadata-less rows plus tagged rows.
        var tagCompletenessGroups = songDbContext.UpvotedSongs.ToArray()
            .GroupBy(s => new { s.UserId, s.Name })
            .Where(group => group.Count() > 1)
            .Where(group => group.Any(s => SongFileMatching.HasNoAlbumOrArtist(s.Artist, s.Album))
                         && group.Any(s => !SongFileMatching.HasNoAlbumOrArtist(s.Artist, s.Album)))
            .ToArray();
        foreach (var group in tagCompletenessGroups)
        {
            var taggedRows = group.Where(s => !SongFileMatching.HasNoAlbumOrArtist(s.Artist, s.Album)).ToArray();
            var tagSignatures = taggedRows.Select(s => (s.Artist, s.Album)).Distinct().ToArray();
            if (tagSignatures.Length != 1)
                continue; // Several differently tagged songs share the file name: a metadata-less row is ambiguous

            // Absorb the metadata-less rows into the tagged row (it carries the tags of the song, so it
            // wins the canonical spot over the metadata-less rows regardless of their score).
            (string fileArtist, string fileAlbum) = tagSignatures[0];
            var (keep, remove) = SongFileMatching.MergeSameSongEntries(group, fileAlbum, fileArtist);
            RemoveRowsWithHistory(songDbContext, keep, remove);
            mergedAway += remove.Length;
        }

        if (mergedAway > 0)
            songDbContext.SaveChanges();

        return mergedAway;
    }

    static void RemoveRowsWithHistory(SongDbContext songDbContext, UpvotedSong keep, UpvotedSong[] remove)
    {
        if (remove.Length == 0)
            return;

        // Drop the history entries of the merged-away rows with them (the kept row keeps its own
        // history). Removed explicitly so the outcome does not depend on the database cascade.
        Guid[] removedIds = remove.Select(entry => entry.SongId).ToArray();
        var orphanedHistory = songDbContext.SongHistoryEntries
            .Where(h => h.SongId != null && removedIds.Contains(h.SongId.Value))
            .ToArray();
        if (orphanedHistory.Length > 0)
            songDbContext.SongHistoryEntries.RemoveRange(orphanedHistory);

        songDbContext.UpvotedSongs.RemoveRange(remove);

        Console.WriteLine($"Healed duplicate rows of \"{keep.Name}\" (artist: {keep.Artist}, album: {keep.Album}, user: {keep.UserId}): kept {keep.SongId}, merged away {remove.Length} row(s) with their history.");
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
