using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayerSyncServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSongLibraryMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SongLibraryMigrations",
                columns: table => new
                {
                    MigrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    MigrationNumber = table.Column<int>(type: "integer", nullable: false),
                    MigrationType = table.Column<string>(type: "text", nullable: false),
                    OldName = table.Column<string>(type: "text", nullable: false),
                    NewName = table.Column<string>(type: "text", nullable: false),
                    Album = table.Column<string>(type: "text", nullable: false),
                    Artist = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongLibraryMigrations", x => x.MigrationId);
                    table.ForeignKey(
                        name: "FK_SongLibraryMigrations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongLibraryMigrations_UserId_MigrationNumber",
                table: "SongLibraryMigrations",
                columns: new[] { "UserId", "MigrationNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongLibraryMigrations");
        }
    }
}
