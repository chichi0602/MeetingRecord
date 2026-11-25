using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingTranscriptionEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TranscriptionDiarize",
                table: "Meeting",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TranscriptionElapsedSeconds",
                table: "Meeting",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptionEngine",
                table: "Meeting",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptionMaxSpeakers",
                table: "Meeting",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranscriptionDiarize",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "TranscriptionElapsedSeconds",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "TranscriptionEngine",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "TranscriptionMaxSpeakers",
                table: "Meeting");
        }
    }
}
