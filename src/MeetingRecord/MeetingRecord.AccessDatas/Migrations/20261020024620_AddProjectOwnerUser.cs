using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectOwnerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerUserId",
                table: "Project",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Project_OwnerUserId",
                table: "Project",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Project_MyUser_OwnerUserId",
                table: "Project",
                column: "OwnerUserId",
                principalTable: "MyUser",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // 舊的手打負責人：剛好對到一個帳號（姓名或帳號）才填，其餘留 null。
            migrationBuilder.Sql(Sql.ProjectOwnerBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Project_MyUser_OwnerUserId",
                table: "Project");

            migrationBuilder.DropIndex(
                name: "IX_Project_OwnerUserId",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Project");
        }
    }
}
