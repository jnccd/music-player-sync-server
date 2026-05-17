using System;
using System.IO;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerSyncServer.Database;

public class SongDbContext : DbContext
{
    public string DbStatus { get; private set; } = "Not connected";
    public DbSet<User> Users { get; set; }
    public DbSet<UpvotedSong> UpvotedSongs { get; set; }
    public DbSet<SongHistoryEntry> SongHistoryEntries { get; set; }

    public SongDbContext(DbContextOptions<SongDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => MusicPlayerSyncInterface.Database.Model.OnModelCreating(modelBuilder);

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (Environment.GetEnvironmentVariable("DB_PROVIDER") == "postgres" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DB_PROVIDER")))
        {
            options.UseNpgsql(Environment.GetEnvironmentVariable("POSTGRES_DB_ACCESS"));
            DbStatus = "Using PostgreSQL DB";
        }
        else if (Environment.GetEnvironmentVariable("DB_PROVIDER") == "sqlite")
        {
            var exePath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? "~") + Path.DirectorySeparatorChar;
            var sqlitePath = (Environment.GetEnvironmentVariable("MUSIC_PLAYER_SQLITE_DB_PATH") ?? exePath) + "song.db";

            options.UseSqlite($"Data Source={sqlitePath}");
            DbStatus = $"Using SQLite DB at {sqlitePath}";
        }
        else
        {
            throw new InvalidOperationException("No valid DB_PROVIDER environment variable set. Use 'sqlite' or 'postgres'.");
        }
    }
}
