using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordTagsAndRoleTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultTeamsJson",
                table: "RoleView",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Categories",
                table: "Project",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Teams",
                table: "Project",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Categories",
                table: "MyTas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Teams",
                table: "MyTas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Categories",
                table: "Meeting",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Teams",
                table: "Meeting",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultTeamsJson",
                table: "RoleView");

            migrationBuilder.DropColumn(
                name: "Categories",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "Teams",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "Categories",
                table: "MyTas");

            migrationBuilder.DropColumn(
                name: "Teams",
                table: "MyTas");

            migrationBuilder.DropColumn(
                name: "Categories",
                table: "Meeting");

            migrationBuilder.DropColumn(
                name: "Teams",
                table: "Meeting");
        }
    }
}
