using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayerSyncServer.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    UserHandle = table.Column<string>(type: "text", nullable: false),
                    UserDisplayName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "UpvotedSongs",
                columns: table => new
                {
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Artist = table.Column<string>(type: "text", nullable: false),
                    Album = table.Column<string>(type: "text", nullable: false),
                    Score = table.Column<float>(type: "real", nullable: false),
                    Streak = table.Column<int>(type: "integer", nullable: false),
                    TotalLikes = table.Column<int>(type: "integer", nullable: false),
                    TotalDislikes = table.Column<int>(type: "integer", nullable: false),
                    DateAdded = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Volume = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpvotedSongs", x => x.SongId);
                    table.ForeignKey(
                        name: "FK_UpvotedSongs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "SongHistoryEntries",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScoreChange = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongHistoryEntries", x => new { x.UserId, x.SongId, x.Date });
                    table.ForeignKey(
                        name: "FK_SongHistoryEntries_UpvotedSongs_SongId",
                        column: x => x.SongId,
                        principalTable: "UpvotedSongs",
                        principalColumn: "SongId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongHistoryEntries_SongId",
                table: "SongHistoryEntries",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_UpvotedSongs_UserId_Name_Artist_Album",
                table: "UpvotedSongs",
                columns: new[] { "UserId", "Name", "Artist", "Album" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongHistoryEntries");

            migrationBuilder.DropTable(
                name: "UpvotedSongs");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
