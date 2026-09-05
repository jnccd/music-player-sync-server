using System;
using System.Collections.Generic;
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
            if (remove.Length > 0)
            {
                mergedAway += remove.Length;
                songDbContext.SaveChanges(); // Persist each group before the next one is processed
            }
        }

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

            // Absorb the duplicate rows into one canonical row. The canonical row is chosen by
            // SongFileMatching.MergeSameSongEntries: the row carrying the song data (score, likes/
            // dislikes, streak, volume) ALWAYS wins - even when it is the metadata-less row - because
            // that data is accumulated from user input over time and cannot be recreated. The file's
            // metadata is adopted onto it AFTER the (possibly tagged) loser row was removed, so the
            // adoption can never collide with the loser's identity.
            (string fileArtist, string fileAlbum) = tagSignatures[0];
            var (keep, remove) = SongFileMatching.MergeSameSongEntries(group, fileAlbum, fileArtist);
            RemoveRowsWithHistory(songDbContext, keep, remove);
            if (remove.Length == 0)
                continue;
            mergedAway += remove.Length;
            songDbContext.SaveChanges(); // Drop the loser row(s) first (their identity is still taken)

            if (SongFileMatching.TryGetTagsToAdoptOnto(keep, group, fileAlbum, fileArtist, out string adoptAlbum, out string adoptArtists))
            {
                // The data-carrying row was the metadata-less one: merge the metadata of the song onto
                // it now that no row with that identity exists anymore.
                keep.Artist = adoptArtists;
                keep.Album = adoptAlbum;
                songDbContext.SaveChanges();
                Console.WriteLine($"Adopted metadata of \"{keep.Name}\" (artist: {keep.Artist}, album: {keep.Album}) onto data-carrying row {keep.SongId}.");
            }
        }

        return mergedAway;
    }

    static void RemoveRowsWithHistory(SongDbContext songDbContext, UpvotedSong keep, UpvotedSong[] remove)
    {
        if (remove.Length == 0)
            return;

        // A merge must not throw the votes of the merged-away rows away: their history entries are
        // re-pointed onto the kept row. EF Core cannot modify a key property (UserId, SongId) of a
        // tracked entity in place, so each moved entry is re-created under the kept row's key (delete +
        // re-add in the same SaveChanges). Entries that collide with the kept row's own history (same
        // account + same date) are the same listening event recorded twice and are dropped as
        // duplicates. The kept row's counters (score/streak/likes/dislikes) are left UNTOUCHED: they
        // are the accumulated values of the row with the most data and may include votes from times
        // before history entries were recorded, so they are never recomputed.
        // Queried per row: EF Core 8 on .NET 10 cannot parameterize "array.Contains(...)" in a query
        // (it tries to compile a ReadOnlySpan closure and throws), so ids are compared one by one.
        var keepDates = new HashSet<DateTimeOffset>(songDbContext.SongHistoryEntries
            .Where(h => h.UserId == keep.UserId && h.SongId == keep.SongId)
            .Select(h => h.Date));

        int movedHistory = 0;
        int droppedDuplicateEntries = 0;
        foreach (UpvotedSong removed in remove)
        {
            var removedHistory = songDbContext.SongHistoryEntries
                .Where(h => h.SongId == removed.SongId)
                .ToArray();
            foreach (SongHistoryEntry entry in removedHistory)
            {
                if (keepDates.Add(entry.Date))
                {
                    songDbContext.SongHistoryEntries.Remove(entry);
                    songDbContext.SongHistoryEntries.Add(new SongHistoryEntry(keep.SongId, entry.ScoreChange, entry.Date, keep.UserId));
                    movedHistory++;
                }
                else
                {
                    songDbContext.SongHistoryEntries.Remove(entry);
                    droppedDuplicateEntries++;
                }
            }
        }

        // Save the history moves BEFORE deleting the rows: PostgreSQL would otherwise cascade-delete
        // the still-referencing history entries when the removed UpvotedSong rows go away.
        if (movedHistory > 0 || droppedDuplicateEntries > 0)
            songDbContext.SaveChanges();

        songDbContext.UpvotedSongs.RemoveRange(remove);

        Console.WriteLine($"Healed duplicate rows of \"{keep.Name}\" (artist: {keep.Artist}, album: {keep.Album}, user: {keep.UserId}): kept {keep.SongId}, merged away {remove.Length} row(s); moved {movedHistory} history entr{(movedHistory == 1 ? "y" : "ies")} onto the kept row{(droppedDuplicateEntries > 0 ? $", dropped {droppedDuplicateEntries} duplicate history entr{(droppedDuplicateEntries == 1 ? "y" : "ies")} (same date)" : "")}.");
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
