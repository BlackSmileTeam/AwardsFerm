using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwardsFerm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialAuthSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Login = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordSalt = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ad_accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GameTitle = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    GameUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    TokenEncrypted = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ad_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ad_accounts_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rsya_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdAccountId = table.Column<long>(type: "INTEGER", nullable: false),
                    TodayReward = table.Column<decimal>(type: "TEXT", nullable: false),
                    MonthReward = table.Column<decimal>(type: "TEXT", nullable: false),
                    TodayShows = table.Column<long>(type: "INTEGER", nullable: false),
                    TodayClicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rsya_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rsya_snapshots_ad_accounts_AdAccountId",
                        column: x => x.AdAccountId,
                        principalTable: "ad_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_slots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdAccountId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ScheduleEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduledStartMsk = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    StopAtMsk = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    AutoRestart = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_slots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_slots_ad_accounts_AdAccountId",
                        column: x => x.AdAccountId,
                        principalTable: "ad_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionSlotId = table.Column<long>(type: "INTEGER", nullable: false),
                    RuntimeSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    GameOverCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_runs_session_slots_SessionSlotId",
                        column: x => x.SessionSlotId,
                        principalTable: "session_slots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ad_accounts_UserId",
                table: "ad_accounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_rsya_snapshots_AdAccountId_CapturedAt",
                table: "rsya_snapshots",
                columns: new[] { "AdAccountId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_session_runs_RuntimeSessionId",
                table: "session_runs",
                column: "RuntimeSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_session_runs_SessionSlotId",
                table: "session_runs",
                column: "SessionSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_session_slots_AdAccountId_ProfileId",
                table: "session_slots",
                columns: new[] { "AdAccountId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Login",
                table: "users",
                column: "Login",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rsya_snapshots");

            migrationBuilder.DropTable(
                name: "session_runs");

            migrationBuilder.DropTable(
                name: "session_slots");

            migrationBuilder.DropTable(
                name: "ad_accounts");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
