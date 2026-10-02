using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyTeamTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Teams",
                table: "PromptTemplate");

            migrationBuilder.DropColumn(
                name: "Teams",
                table: "Meeting");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Teams",
                table: "PromptTemplate",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Teams",
                table: "Meeting",
                type: "TEXT",
                nullable: true);
        }
    }
}
