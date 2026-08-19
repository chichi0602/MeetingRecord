using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TwoFactorEnabled",
                table: "MyUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TwoFactorSecret",
                table: "MyUser",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TwoFactorEnabled",
                table: "MyUser");

            migrationBuilder.DropColumn(
                name: "TwoFactorSecret",
                table: "MyUser");
        }
    }
}
