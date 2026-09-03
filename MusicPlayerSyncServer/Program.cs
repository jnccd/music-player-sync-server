using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayerSyncServer;
using MusicPlayerSyncServer.Database;

Console.WriteLine("MusicPlayerSyncServer Startup!");
DotNetEnv.Env.Load("../.env", new(setEnvVars: true));

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureWebhost();
builder.RegisterServices();

var app = builder.Build();
app.RegisterMiddlewares();
app.ConfigureWebApp();
app.RegisterNotesEndpointsV1(app.Services);

// Heal duplicate UpvotedSong rows that slipped in before duplicate uploads were rejected (same user,
// file name and tags registered by different clients under different SongIds). Idempotent: once the
// data is clean this is a no-op. See UpvotedSongDeduplicator.
try
{
    using var healScope = app.Services.CreateScope();
    var songDbContext = healScope.ServiceProvider.GetRequiredService<SongDbContext>();
    int mergedAway = UpvotedSongDeduplicator.MergeDuplicateUpvotedSongs(songDbContext);
    UpvotedSongDeduplicator.EnsureUniqueSongIndex(songDbContext);
    Console.WriteLine(mergedAway > 0
        ? $"Healed {mergedAway} duplicate upvotedSong row(s) at startup."
        : "No duplicate upvotedSong rows to heal at startup.");
}
catch (Exception ex)
{
    Console.WriteLine($"UpvotedSong duplicate heal failed at startup: {ex}");
}

app.Run();
