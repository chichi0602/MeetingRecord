using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddMeeting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Meeting",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    MeetingDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Categories = table.Column<string>(type: "TEXT", nullable: true),
                    Teams = table.Column<string>(type: "TEXT", nullable: true),
                    MediaOriginalFileName = table.Column<string>(type: "TEXT", nullable: true),
                    MediaStoredFileName = table.Column<string>(type: "TEXT", nullable: true),
                    MediaRelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    MediaContentType = table.Column<string>(type: "TEXT", nullable: true),
                    MediaFileSize = table.Column<long>(type: "INTEGER", nullable: true),
                    TranscriptRelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptionStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    TranscriptionError = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptionStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TranscriptionCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Meeting", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Meeting");
        }
    }
}
