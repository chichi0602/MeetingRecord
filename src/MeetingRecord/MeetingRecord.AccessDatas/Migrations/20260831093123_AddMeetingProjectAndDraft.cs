using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingProjectAndDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DraftCompletedAt",
                table: "Meeting",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftContent",
                table: "Meeting",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftError",
                table: "Meeting",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftPromptTemplateId",
                table: "Meeting",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftPromptTemplateName",
                table: "Meeting",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DraftStartedAt",
                table: "Meeting",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftStatus",
                table: "Meeting",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProjectId",
                table: "Meeting",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Meeting_ProjectId",
                table: "Meeting",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Meeting_Project_ProjectId",
                table: "Meeting",
                column: "ProjectId",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Meeting_Project_ProjectId",
                table: "Meeting");

            migrationBuilder.DropIndex(
                name: "IX_Meeting_ProjectId",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "DraftCompletedAt",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "DraftContent",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "DraftError",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "DraftPromptTemplateId",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "DraftPromptTemplateName",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "DraftStartedAt",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "DraftStatus",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Meeting");
        }
    }
}
