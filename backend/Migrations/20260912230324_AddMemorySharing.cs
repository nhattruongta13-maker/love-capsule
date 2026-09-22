using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoveCapsule.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMemorySharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PartnerCanEdit",
                table: "Memories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RelationshipId",
                table: "Memories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                table: "Memories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PartnerCanEdit",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "RelationshipId",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "Memories");
        }
    }
}
