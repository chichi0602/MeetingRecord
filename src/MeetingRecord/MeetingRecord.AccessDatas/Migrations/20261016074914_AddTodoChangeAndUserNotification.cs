using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddTodoChangeAndUserNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TodoChange",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TodoId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChangedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChangedByName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Via = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SourceMeetingId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceMeetingTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoChange", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoChange_Todo_TodoId",
                        column: x => x.TodoId,
                        principalTable: "Todo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserNotification",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TodoId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotification", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotification_MyUser_UserId",
                        column: x => x.UserId,
                        principalTable: "MyUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodoChange_Field_ChangedAt",
                table: "TodoChange",
                columns: new[] { "Field", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoChange_TodoId_ChangedAt",
                table: "TodoChange",
                columns: new[] { "TodoId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotification_CreatedAt",
                table: "UserNotification",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotification_UserId_ReadAt_CreatedAt",
                table: "UserNotification",
                columns: new[] { "UserId", "ReadAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodoChange");

            migrationBuilder.DropTable(
                name: "UserNotification");
        }
    }
}
