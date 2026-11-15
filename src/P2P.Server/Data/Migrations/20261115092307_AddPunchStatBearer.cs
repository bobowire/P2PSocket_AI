using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P2P.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPunchStatBearer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "bearer",
                table: "punch_stats",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bearer",
                table: "punch_stats");
        }
    }
}
