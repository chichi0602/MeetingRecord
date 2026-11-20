using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingDraftVersionsAndSpeakerClips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpeakerClips",
                table: "Meeting",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MeetingDraftVersion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MeetingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    PromptTemplateName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Attendees = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ContentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SavedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    SavedByName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SavedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingDraftVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingDraftVersion_Meeting_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meeting",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingDraftVersion_MeetingId_SavedAt",
                table: "MeetingDraftVersion",
                columns: new[] { "MeetingId", "SavedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingDraftVersion");

            migrationBuilder.DropColumn(
                name: "SpeakerClips",
                table: "Meeting");
        }
    }
}
