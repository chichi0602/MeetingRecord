using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProjectDescriptionPriorityCategoriesTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Categories",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "Teams",
                table: "Project");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Categories",
                table: "Project",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Project",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "Project",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Teams",
                table: "Project",
                type: "TEXT",
                nullable: true);
        }
    }
}
