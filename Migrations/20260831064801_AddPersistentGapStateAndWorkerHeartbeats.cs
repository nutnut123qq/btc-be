using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistentGapStateAndWorkerHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KlineGapStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EndOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    MissingBars = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRetryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FirstDetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KlineGapStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerHeartbeats",
                columns: table => new
                {
                    WorkerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSucceededAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDurationMs = table.Column<long>(type: "bigint", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHeartbeats", x => x.WorkerName);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KlineGapStates_Status_NextRetryAtUtc",
                table: "KlineGapStates",
                columns: new[] { "Status", "NextRetryAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_KlineGapStates_Symbol_Timeframe_StartOpenTimeMs_EndOpenTime~",
                table: "KlineGapStates",
                columns: new[] { "Symbol", "Timeframe", "StartOpenTimeMs", "EndOpenTimeMs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KlineGapStates");

            migrationBuilder.DropTable(
                name: "WorkerHeartbeats");
        }
    }
}
