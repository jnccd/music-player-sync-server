using System.ComponentModel.DataAnnotations;
using EzAuth;
using EzAuth.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicPlayerSyncInterface;
using MusicPlayerSyncInterface.DTOs;
using MusicPlayerSyncInterface.DTOs.Composites;
using MusicPlayerSyncServer.Database;
using MusicPlayerSyncServer.Services.Auth;

namespace MusicPlayerSyncServer;

public static class MusicPlayerSyncEndpointsV1
{
    const string ROUTE_VERSION_PREFIX = "/v1";

    public static void RegisterNotesEndpointsV1(this IEndpointRouteBuilder routes, IServiceProvider services)
    {
        var version1Api = routes.MapGroup(ROUTE_VERSION_PREFIX);

        version1Api.MapGet($"/authBackend", (
           IOptions<AuthOptions> authOptions) =>
        {
            return Results.Ok(new EzAuthAddress
            {
                RealmUrl = authOptions.Value.AuthBackendRealmUrl,
                Client = authOptions.Value.AuthBackendClient
            });
        });

        version1Api.MapPost($"/sync/init", (
            [FromHeader(Name = "Authorization")] string? authTokenHeader,
            [FromBody] SyncInitRequest request,
            [FromServices] AuthService auth,
            [FromServices] SongDbContext songDbContext,
            HttpClient httpClient) =>
        {
            Console.WriteLine($"Received sync init request with {request?.Songs?.Length} songs and {request?.HistoryEntries?.Length} history entries.");
            return auth?.GetUser(authTokenHeader, httpClient, authedUser =>
            {
                request?.Songs.ToList().ForEach(s =>
                {
                    s.UserId = authedUser.UserId;
                    s.DateAdded = s.DateAdded?.UtcDateTime;
                });
                request?.HistoryEntries.ToList().ForEach(h =>
                {
                    h.UserId = authedUser.UserId;
                    h.Date = h.Date.UtcDateTime;
                });

                if (request?.Songs == null || request?.HistoryEntries == null)
                    return Results.BadRequest("Songs and history entries cannot be null.");

                if (!request.Songs.Any() || !request.HistoryEntries.Any())
                    return Results.BadRequest("Songs and history entries cannot be empty.");

                if (songDbContext.UpvotedSongs.Where(x => x.UserId == authedUser.UserId).Any())
                    return Results.Conflict("User already has upvoted those songs in the database.");

                if (songDbContext.SongHistoryEntries.Where(h => h.UserId == authedUser.UserId).Any())
                    return Results.Conflict("User already has history entries in the database.");

                songDbContext.UpvotedSongs.AddRange(request.Songs);
                songDbContext.SongHistoryEntries.AddRange(request.HistoryEntries);
                songDbContext.SaveChanges();

                return Results.Ok();
            });
        });

        version1Api.MapGet($"/sync/pull", (
            [FromHeader(Name = "Authorization")] string? authTokenHeader,
            [FromServices] AuthService auth,
            [FromServices] SongDbContext songDbContext,
            HttpClient httpClient) =>
        {
            return auth?.GetUser(authTokenHeader, httpClient, authedUser =>
            {
                var songs = songDbContext.UpvotedSongs.Where(s => s.UserId == authedUser.UserId).ToArray();
                var historyEntries = songDbContext.SongHistoryEntries.Where(h => h.UserId == authedUser.UserId).ToArray();
                var migrations = songDbContext.SongLibraryMigrations.Where(m => m.UserId == authedUser.UserId).OrderBy(m => m.MigrationNumber).ToArray();

                return Results.Ok(new SyncPullResponse(authedUser, songs, historyEntries, migrations));
            });
        });

        version1Api.MapPost($"/sync/song-library-migration", (
            [FromHeader(Name = "Authorization")] string? authTokenHeader,
            [FromBody] SongLibraryMigration migration,
            [FromServices] AuthService auth,
            [FromServices] SongDbContext songDbContext,
            HttpClient httpClient) =>
        {
            return auth?.GetUser(authTokenHeader, httpClient, authedUser =>
            {
                migration.UserId = authedUser.UserId;

                if (string.IsNullOrWhiteSpace(migration.OldName))
                    return Results.BadRequest("OldName must not be empty.");
                if (migration.SongId == Guid.Empty)
                    return Results.BadRequest("SongId must not be empty.");
                if (migration.MigrationType != SongLibraryMigrationType.Rename && migration.MigrationType != SongLibraryMigrationType.Delete)
                    return Results.BadRequest($"Unknown migration type {migration.MigrationType}.");
                if (migration.MigrationType == SongLibraryMigrationType.Rename)
                {
                    if (string.IsNullOrWhiteSpace(migration.NewName))
                        return Results.BadRequest("NewName must not be empty for a Rename migration.");
                    if (migration.OldName == migration.NewName)
                        return Results.BadRequest("OldName and NewName must differ.");
                }

                // Retried requests (same MigrationId, e.g. after a lost response) just return the already created migration.
                var existingMigration = songDbContext.SongLibraryMigrations.FirstOrDefault(m => m.UserId == migration.UserId && m.MigrationId == migration.MigrationId);
                if (existingMigration != null)
                    return Results.Ok(existingMigration);

                // A migration always refers to one specific UpvotedSong entry (see SongLibraryMigration.SongId).
                // The entry has to exist and still carry the old file name; otherwise the migration is refused,
                // e.g. when the song entry was never synced to the server yet.
                var upvotedSong = songDbContext.UpvotedSongs.FirstOrDefault(s => s.UserId == migration.UserId && s.SongId == migration.SongId);
                if (upvotedSong == null)
                    return Results.Conflict("The song entry this migration refers to does not exist on the server (yet). Make sure the song was synced and try again.");
                if (upvotedSong.Name != migration.OldName)
                    return Results.Conflict($"The song entry this migration refers to currently has the name \"{upvotedSong.Name}\", not \"{migration.OldName}\".");

                // Snapshot the entries album/artist into the migration. A file rename or delete does not change
                // the tags of the song file, so clients can use the snapshot to identify the files that really
                // belong to this entry (a file with the same name but different tags is a different song).
                migration.Artist = upvotedSong.Artist;
                migration.Album = upvotedSong.Album;

                // Apply the actual song library change to that entry here: a rename changes the entries file
                // name, a delete removes the entry (and with it its history entries, via the database cascade).
                // Only if that succeeded (and only then) is the migration itself committed.
                if (migration.MigrationType == SongLibraryMigrationType.Rename)
                {
                    var clashingSong = songDbContext.UpvotedSongs.FirstOrDefault(s =>
                        s.UserId == migration.UserId && s.SongId != upvotedSong.SongId && s.Name == migration.NewName && s.Artist == upvotedSong.Artist && s.Album == upvotedSong.Album);
                    if (clashingSong != null)
                        return Results.Conflict($"A song with the name {migration.NewName} already exists (artist: {upvotedSong.Artist}, album: {upvotedSong.Album}).");

                    upvotedSong.Name = migration.NewName;
                }
                else if (migration.MigrationType == SongLibraryMigrationType.Delete)
                {
                    // Delete migrations only carry an OldName. The NewName column is not nullable, so store an empty string.
                    migration.NewName = "";
                    songDbContext.UpvotedSongs.Remove(upvotedSong);
                }

                // Assign the next migration number for this user (per-user stream). The unique index on
                // (UserId, MigrationNumber) guards against two clients racing, in which case we simply
                // recompute the number and try again (SaveChanges runs in a transaction, so a failed
                // attempt has no side effects).
                songDbContext.SongLibraryMigrations.Add(migration);
                for (int attempt = 0; ; attempt++)
                {
                    migration.MigrationNumber = (songDbContext.SongLibraryMigrations
                        .Where(m => m.UserId == migration.UserId)
                        .Max(m => (int?)m.MigrationNumber) ?? 0) + 1;

                    try
                    {
                        songDbContext.SaveChanges();
                        break;
                    }
                    catch (DbUpdateException ex) when (attempt < 2)
                    {
                        Console.WriteLine($"Migration number collision for user {migration.UserId}, retrying ({ex.Message})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to save song library migration for user {migration.UserId}: {ex}");
                        return Results.Problem("Failed to save song library migration. Please try again.");
                    }
                }

                return Results.Ok(migration);
            });
        });

        version1Api.MapPost($"/sync/new-song", (
            [FromHeader(Name = "Authorization")] string? authTokenHeader,
            [FromBody] UpvotedSong song,
            [FromServices] AuthService auth,
            [FromServices] SongDbContext songDbContext,
            HttpClient httpClient) =>
        {
            return auth?.GetUser(authTokenHeader, httpClient, authedUser =>
            {
                song.UserId = authedUser.UserId;
                song.DateAdded = song.DateAdded?.UtcDateTime;

                if (songDbContext.UpvotedSongs.Where(x => x.SongId == song.SongId).Any())
                    return Results.Conflict();

                // A song is identified by its file name plus album/artist tags, per user. If the same
                // song already exists (e.g. another client of this account registered the same file, or
                // a client that was offline retries an upload that already went through), reject the new
                // upload and return the existing row as the body, so clients can remap queued data
                // (votes etc.) that still refers to their own SongId of this song.
                UpvotedSong? alreadyExisting = songDbContext.UpvotedSongs.FirstOrDefault(x =>
                    x.UserId == song.UserId && x.Name == song.Name && x.Artist == song.Artist && x.Album == song.Album);
                if (alreadyExisting == null && SongFileMatching.HasNoAlbumOrArtist(song.Artist, song.Album))
                {
                    // The upload carries no album/artist metadata, so it cannot be matched by its exact
                    // tags. If rows of the same file name already exist and all of them share ONE tag
                    // signature, this upload is almost certainly that same song registered without its
                    // tags (older clients did not read tags from the file) - treat it as a duplicate so
                    // no second, metadata-less row is created. When rows of several different signatures
                    // share the file name, the upload could be another song and is accepted.
                    var sameNameRows = songDbContext.UpvotedSongs
                        .Where(x => x.UserId == song.UserId && x.Name == song.Name)
                        .ToArray(); // Materialize first: SongFileMatching is not translatable to SQL
                    var sameNameTaggedRows = sameNameRows
                        .Where(x => !SongFileMatching.HasNoAlbumOrArtist(x.Artist, x.Album))
                        .ToArray();
                    if (sameNameTaggedRows.Length > 0 && sameNameTaggedRows.Select(x => (x.Artist, x.Album)).Distinct().Count() == 1)
                        alreadyExisting = sameNameTaggedRows.First();
                }
                if (alreadyExisting != null)
                {
                    Console.WriteLine($"Rejected duplicate upvotedSong upload \"{song.Name}\" (artist: {song.Artist}, album: {song.Album}) for user {song.UserId} - already exists as {alreadyExisting.SongId}.");
                    return Results.Json(alreadyExisting, statusCode: StatusCodes.Status409Conflict);
                }

                try
                {
                    songDbContext.UpvotedSongs.Add(song);
                    songDbContext.SaveChanges();
                }
                catch (DbUpdateException ex)
                {
                    // Two clients raced and both inserted the same song (only possible while the unique
                    // index is missing): treat it like the duplicate check above instead of failing hard,
                    // so the loser remaps to the row of the winner.
                    Console.WriteLine($"Duplicate upvotedSong upload \"{song.Name}\" raced with another insert ({ex.Message}).");
                    alreadyExisting = songDbContext.UpvotedSongs.FirstOrDefault(x =>
                        x.UserId == song.UserId && x.Name == song.Name && x.Artist == song.Artist && x.Album == song.Album);
                    if (alreadyExisting != null)
                        return Results.Json(alreadyExisting, statusCode: StatusCodes.Status409Conflict);
                    Console.WriteLine($"Failed to save upvotedSong upload \"{song.Name}\": {ex}");
                    return Results.Problem("Failed to save the song. Please try again.");
                }

                return Results.Ok();
            });
        });

        version1Api.MapPost($"/sync/vote", (
            [FromHeader(Name = "Authorization")] string? authTokenHeader,
            [FromBody] SongHistoryEntry entry,
            [FromServices] AuthService auth,
            [FromServices] SongDbContext songDbContext,
            HttpClient httpClient) =>
        {
            return auth?.GetUser(authTokenHeader, httpClient, authedUser =>
            {
                entry.UserId = authedUser.UserId;
                entry.Date = entry.Date.UtcDateTime;
                if (songDbContext.SongHistoryEntries.Where(h => h.UserId == entry.UserId && h.SongId == entry.SongId && h.Date == entry.Date).Any())
                    return Results.Conflict();

                var upvotedSong = songDbContext.UpvotedSongs.FirstOrDefault(s => s.UserId == entry.UserId && s.SongId == entry.SongId);
                if (upvotedSong == null)
                    return Results.NotFound("No upvoted song found for this entry. Make sure to add the song first with /sync/new-song.");

                upvotedSong.Score += entry.ScoreChange;
                upvotedSong.TotalLikes += entry.ScoreChange > 0 ? 1 : 0;
                upvotedSong.TotalDislikes += entry.ScoreChange < 0 ? 1 : 0;
                if (entry.ScoreChange > 0)
                {
                    if (upvotedSong.Streak < 0)
                        upvotedSong.Streak = 1;
                    else
                        upvotedSong.Streak += 1;
                }
                else if (entry.ScoreChange < 0)
                {
                    if (upvotedSong.Streak > 0)
                        upvotedSong.Streak = -1;
                    else
                        upvotedSong.Streak -= 1;
                }

                songDbContext.SongHistoryEntries.Add(entry);
                songDbContext.SaveChanges();

                return Results.Ok();
            });
        });

        version1Api.MapPut($"/sync/volume", (
            [FromHeader(Name = "Authorization")] string? authTokenHeader,
            [FromBody] UpdateVolumeRequest request,
            [FromServices] AuthService auth,
            [FromServices] SongDbContext songDbContext,
            HttpClient httpClient) =>
        {
            return auth?.GetUser(authTokenHeader, httpClient, authedUser =>
            {
                if (request.NewVolume <= 0)
                    return Results.BadRequest("Volume must be positive.");

                var song = songDbContext.UpvotedSongs.FirstOrDefault(s => s.UserId == authedUser.UserId && s.SongId == request.SongId);
                if (song == null)
                    return Results.NotFound();

                song.Volume = request.NewVolume;
                songDbContext.SaveChanges();

                return Results.Ok();
            });
        });
    }
}
