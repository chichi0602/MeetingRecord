using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectPrimaryTeamAndCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimary",
                table: "ProjectTeam",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Categories",
                table: "Project",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CategoryTeam",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryTeam", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryTeam_Category_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Category",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryTeam_Team_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Team",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryTeam_CategoryId_TeamId",
                table: "CategoryTeam",
                columns: new[] { "CategoryId", "TeamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CategoryTeam_TeamId",
                table: "CategoryTeam",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryTeam");

            migrationBuilder.DropColumn(
                name: "IsPrimary",
                table: "ProjectTeam");

            migrationBuilder.DropColumn(
                name: "Categories",
                table: "Project");
        }
    }
}
