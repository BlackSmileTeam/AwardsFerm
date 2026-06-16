using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwardsFerm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionSlotProxyEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProxyEnabled",
                table: "session_slots",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProxyEnabled",
                table: "session_slots");
        }
    }
}
