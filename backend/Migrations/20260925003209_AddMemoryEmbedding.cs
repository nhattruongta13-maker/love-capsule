using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoveCapsule.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Embedding",
                table: "Memories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "Memories");
        }
    }
}
