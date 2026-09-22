using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoveCapsule.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerUserId",
                table: "Memories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memories_OwnerUserId",
                table: "Memories",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Memories_Users_OwnerUserId",
                table: "Memories",
                column: "OwnerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Memories_Users_OwnerUserId",
                table: "Memories");

            migrationBuilder.DropIndex(
                name: "IX_Memories_OwnerUserId",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Memories");
        }
    }
}
