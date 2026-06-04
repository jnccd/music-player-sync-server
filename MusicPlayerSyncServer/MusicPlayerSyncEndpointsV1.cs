using System.ComponentModel.DataAnnotations;
using EzAuth;
using EzAuth.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
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

                return Results.Ok(new SyncPullResponse(authedUser, songs, historyEntries));
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

                songDbContext.UpvotedSongs.Add(song);
                songDbContext.SaveChanges();

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
