using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTeamRemoveDefaultTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ 刻意不在這裡 DropTable("ProjectMember")：舊的專案成員要留給啟動時的
            //    DataGroupConversionService 轉成同名資料群組，轉完由它刪表（只會跑一次）。

            migrationBuilder.DropColumn(
                name: "DefaultTeamsJson",
                table: "RoleView");

            migrationBuilder.CreateTable(
                name: "ProjectTeam",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTeam", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTeam_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectTeam_Team_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Team",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTeam_ProjectId_TeamId",
                table: "ProjectTeam",
                columns: new[] { "ProjectId", "TeamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTeam_TeamId",
                table: "ProjectTeam",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectTeam");

            migrationBuilder.AddColumn<string>(
                name: "DefaultTeamsJson",
                table: "RoleView",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // ProjectMember 不在這裡重建：Up 沒刪它（見上方說明），轉換後也已不存在。
        }
    }
}
