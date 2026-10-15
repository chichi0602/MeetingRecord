using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCallLogAndMeetingDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MediaDurationSeconds",
                table: "Meeting",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CallId",
                table: "AiUsageLog",
                type: "TEXT",
                nullable: true);

            // 1.0.29：既有會議的影音時長從用量帳本回填（儀表板「會議時數」改讀 Meeting 自己的欄位，帳本可以被清除了）。
            migrationBuilder.Sql(Sql.MeetingDurationBackfill.Sql);

            migrationBuilder.CreateTable(
                name: "AiCallLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CallId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Feature = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MeetingId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelatedInfo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ThreadKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    HttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ElapsedMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    IsResponsePartial = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ContentFile = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCallLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLog_CallId",
                table: "AiUsageLog",
                column: "CallId");

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_CallId",
                table: "AiCallLog",
                column: "CallId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_MeetingId",
                table: "AiCallLog",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_OccurredAt",
                table: "AiCallLog",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_ProjectId",
                table: "AiCallLog",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_ThreadKey",
                table: "AiCallLog",
                column: "ThreadKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiCallLog");

            migrationBuilder.DropIndex(
                name: "IX_AiUsageLog_CallId",
                table: "AiUsageLog");

            migrationBuilder.DropColumn(
                name: "MediaDurationSeconds",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "CallId",
                table: "AiUsageLog");
        }
    }
}
