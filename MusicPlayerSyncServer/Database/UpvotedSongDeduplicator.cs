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

        // 2. Tag-completeness duplicates: same user and file name, where one row recorded fewer tags than
        // another (tagless rows of older clients, or an artist/album that was pruned on one client).
        // There is deliberately NO "must contain a fully metadata-less row" precondition: a row that has
        // only an album (or only an artist) is just as much a partial row. TryGetCombinedTags below
        // decides whether the rows can be one song - partial rows have to share a field, and rows whose
        // non-empty tags contradict each other are genuinely different songs. Exact-identity duplicates
        // were already removed by pass 1.
        var tagCompletenessGroups = songDbContext.UpvotedSongs.ToArray()
            .GroupBy(s => new { s.UserId, s.Name })
            .Where(group => group.Count() > 1)
            .ToArray();
        foreach (var group in tagCompletenessGroups)
        {
            // The tagged rows of the name must not CONTRADICT each other (both carry a field with
            // different non-empty values) for them to be one song; empty fields are ignored, since rows
            // of one song can differ by a pruned artist/album on one of the clients.
            if (!SongFileMatching.TryGetCombinedTags(group, out string combinedArtist, out string combinedAlbum))
                continue; // Conflicting tags on the same file name: genuinely different songs

            // Merge the rows keeping the **data-carrying** canonical row (score/history always
            // survives; the merged-away row's history is moved onto it) and fill the empty tag fields
            // of the kept row from the combined metadata AFTER the other row(s) were removed, so the
            // fill can never collide with the loser's identity.
            var (keep, remove) = SongFileMatching.MergeSameSongEntries(group, combinedAlbum, combinedArtist);
            RemoveRowsWithHistory(songDbContext, keep, remove);
            if (remove.Length == 0)
                continue;
            mergedAway += remove.Length;
            songDbContext.SaveChanges(); // Drop the loser row(s) first (their identity is still taken)

            if (SongFileMatching.TryFillMissingTags(keep, combinedAlbum, combinedArtist, out string? artistToSet, out string? albumToSet))
            {
                if (artistToSet != null)
                    keep.Artist = artistToSet;
                if (albumToSet != null)
                    keep.Album = albumToSet;
                songDbContext.SaveChanges();
                Console.WriteLine($"Filled metadata of \"{keep.Name}\" (artist: {keep.Artist}, album: {keep.Album}) onto data-carrying row {keep.SongId}.");
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
        // Moved entries get a FRESH sequence (continuing the user's stream) instead of keeping the old
        // one: incremental clients are already past that cursor, so only a new sequence delivers the
        // moved history to them (they keep/ignore their local entry under the old SongId).
        long nextSequence = SongHistorySequencer.GetMaxSequence(songDbContext, keep.UserId);
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
                    var movedEntry = new SongHistoryEntry(keep.SongId, entry.ScoreChange, entry.Date, keep.UserId);
                    songDbContext.SongHistoryEntries.Add(movedEntry);
                    SongHistorySequencer.AssignSequence(songDbContext, movedEntry, ++nextSequence);
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
