using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecord.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddTodoParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentTodoId",
                table: "Todo",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Todo_ParentTodoId",
                table: "Todo",
                column: "ParentTodoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Todo_Todo_ParentTodoId",
                table: "Todo",
                column: "ParentTodoId",
                principalTable: "Todo",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Todo_Todo_ParentTodoId",
                table: "Todo");

            migrationBuilder.DropIndex(
                name: "IX_Todo_ParentTodoId",
                table: "Todo");

            migrationBuilder.DropColumn(
                name: "ParentTodoId",
                table: "Todo");
        }
    }
}
