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
    public DbSet<SongLibraryMigration> SongLibraryMigrations { get; set; }

    public SongDbContext(DbContextOptions<SongDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        MusicPlayerSyncInterface.Database.Model.OnModelCreating(modelBuilder);

        // The SongLibraryMigration table only exists on the server. The clients track the migration state
        // with a ".song-library.music-player-config" file in their song library instead of a local db table,
        // which is why this entity is not part of the shared MusicPlayerSyncInterface.Database.Model.
        modelBuilder.Entity<SongLibraryMigration>(migration =>
        {
            migration.HasKey(x => x.MigrationId);
            migration.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.NoAction);
            migration.HasIndex(x => new { x.UserId, x.MigrationNumber })
                .IsUnique();
            migration.Property(x => x.MigrationType)
                .HasConversion<string>();
        });
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        Console.WriteLine("Configuring database connection...");
        if (Environment.GetEnvironmentVariable("DB_PROVIDER") == "postgres" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DB_PROVIDER")))
        {
            Console.WriteLine("Using PostgreSQL database provider.");
            options.UseNpgsql(Environment.GetEnvironmentVariable("POSTGRES_DB_ACCESS"));
            DbStatus = "Using PostgreSQL DB";
        }
        else if (Environment.GetEnvironmentVariable("DB_PROVIDER") == "sqlite")
        {
            var exePath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? "~") + Path.DirectorySeparatorChar;
            var sqlitePath = (Environment.GetEnvironmentVariable("MUSIC_PLAYER_SQLITE_DB_PATH") ?? exePath) + "song.db";
            Console.WriteLine($"Using SQLite database provider at {sqlitePath}.");

            options.UseSqlite($"Data Source={sqlitePath}");
            DbStatus = $"Using SQLite DB at {sqlitePath}";
        }
        else
        {
            throw new InvalidOperationException("No valid DB_PROVIDER environment variable set. Use 'sqlite' or 'postgres'.");
        }
    }
}
