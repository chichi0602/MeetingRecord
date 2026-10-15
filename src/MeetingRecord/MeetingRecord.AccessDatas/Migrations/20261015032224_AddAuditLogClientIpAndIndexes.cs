using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogClientIpAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientIp",
                table: "AuditLog",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Action",
                table: "AuditLog",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_ActorUserId_OccurredAt",
                table: "AuditLog",
                columns: new[] { "ActorUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_OccurredAt",
                table: "AuditLog",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLog_Action",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_ActorUserId_OccurredAt",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_OccurredAt",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "ClientIp",
                table: "AuditLog");
        }
    }
}
