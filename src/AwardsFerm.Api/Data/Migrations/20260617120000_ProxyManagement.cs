using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwardsFerm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProxyManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "proxies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Scheme = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Login = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PasswordEncrypted = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    Timezone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Locale = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    LocationLabel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proxies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_proxies_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_proxies_UserId_Name",
                table: "proxies",
                columns: new[] { "UserId", "Name" });

            migrationBuilder.AddColumn<long>(
                name: "ProxyId",
                table: "session_slots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_slots_ProxyId",
                table: "session_slots",
                column: "ProxyId");

            migrationBuilder.AddForeignKey(
                name: "FK_session_slots_proxies_ProxyId",
                table: "session_slots",
                column: "ProxyId",
                principalTable: "proxies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_session_slots_proxies_ProxyId",
                table: "session_slots");

            migrationBuilder.DropTable(
                name: "proxies");

            migrationBuilder.DropIndex(
                name: "IX_session_slots_ProxyId",
                table: "session_slots");

            migrationBuilder.DropColumn(
                name: "ProxyId",
                table: "session_slots");
        }
    }
}
