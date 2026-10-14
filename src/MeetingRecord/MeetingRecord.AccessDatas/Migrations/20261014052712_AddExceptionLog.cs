using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddExceptionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExceptionLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Signature = table.Column<string>(type: "TEXT", nullable: false),
                    ExceptionType = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Page = table.Column<string>(type: "TEXT", nullable: true),
                    Operation = table.Column<string>(type: "TEXT", nullable: true),
                    LoggerName = table.Column<string>(type: "TEXT", nullable: true),
                    Account = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    StackTraceFile = table.Column<string>(type: "TEXT", nullable: true),
                    OccurrenceCount = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstOccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastOccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastTraceId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExceptionLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionLog_LastOccurredAt",
                table: "ExceptionLog",
                column: "LastOccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionLog_Signature",
                table: "ExceptionLog",
                column: "Signature",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExceptionLog");
        }
    }
}
