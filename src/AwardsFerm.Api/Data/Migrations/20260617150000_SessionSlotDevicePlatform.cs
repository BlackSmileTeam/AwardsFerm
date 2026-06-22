using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwardsFerm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionSlotDevicePlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DevicePlatform",
                table: "session_slots",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Random");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DevicePlatform",
                table: "session_slots");
        }
    }
}
