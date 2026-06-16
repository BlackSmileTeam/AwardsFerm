using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwardsFerm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionIpAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_ip_audits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuntimeSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AdAccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PublicIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_ip_audits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_ip_audits_AdAccountId_CapturedAt",
                table: "session_ip_audits",
                columns: new[] { "AdAccountId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_session_ip_audits_RuntimeSessionId_CapturedAt",
                table: "session_ip_audits",
                columns: new[] { "RuntimeSessionId", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_ip_audits");
        }
    }
}
