using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTodoCategoriesTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Categories",
                table: "Todo");

            migrationBuilder.DropColumn(
                name: "Teams",
                table: "Todo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Categories",
                table: "Todo",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Teams",
                table: "Todo",
                type: "TEXT",
                nullable: true);
        }
    }
}
