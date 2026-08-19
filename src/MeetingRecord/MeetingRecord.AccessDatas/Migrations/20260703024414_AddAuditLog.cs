using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorAccount = table.Column<string>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: true),
                    TargetId = table.Column<string>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");
        }
    }
}
