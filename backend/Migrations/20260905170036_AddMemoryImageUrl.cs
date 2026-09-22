using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoveCapsule.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryImageUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "Memories",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "Memories");
        }
    }
}
