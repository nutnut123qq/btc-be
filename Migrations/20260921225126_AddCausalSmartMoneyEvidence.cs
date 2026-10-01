using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddCausalSmartMoneyEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CausalSmartMoneyEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EventId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OriginTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    AvailableTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    ReferenceTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    Price = table.Column<double>(type: "double precision", nullable: false),
                    HighPrice = table.Column<double>(type: "double precision", nullable: true),
                    LowPrice = table.Column<double>(type: "double precision", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MitigatedAtMs = table.Column<long>(type: "bigint", nullable: true),
                    MitigationSourceOpenTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    CalculationVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SegmentStartOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EvaluatedThroughCloseTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    AnalysisCandleCount = table.Column<int>(type: "integer", nullable: false),
                    DecisionSourceCandleCount = table.Column<int>(type: "integer", nullable: false),
                    DecisionSourceOpenTimeMsJson = table.Column<string>(type: "text", nullable: false),
                    DecisionEvidenceJson = table.Column<string>(type: "text", nullable: false),
                    DecisionEvidenceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CausalSmartMoneyEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CausalSmartMoneyRebuildCheckpoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CalculationVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastProcessedOpenTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    CoverageStartOpenTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    LatestSegmentStartOpenTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    ProcessedCandleCount = table.Column<long>(type: "bigint", nullable: false),
                    MaterializedEventCount = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CausalSmartMoneyRebuildCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CausalSmartMoneyEvents_Symbol_Timeframe_AvailableTimeMs",
                table: "CausalSmartMoneyEvents",
                columns: new[] { "Symbol", "Timeframe", "AvailableTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_CausalSmartMoneyEvents_Symbol_Timeframe_EventId_Calculation~",
                table: "CausalSmartMoneyEvents",
                columns: new[] { "Symbol", "Timeframe", "EventId", "CalculationVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CausalSmartMoneyEvents_Symbol_Timeframe_MitigatedAtMs",
                table: "CausalSmartMoneyEvents",
                columns: new[] { "Symbol", "Timeframe", "MitigatedAtMs" });

            migrationBuilder.CreateIndex(
                name: "IX_CausalSmartMoneyRebuildCheckpoints_Symbol_Timeframe_Calcula~",
                table: "CausalSmartMoneyRebuildCheckpoints",
                columns: new[] { "Symbol", "Timeframe", "CalculationVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CausalSmartMoneyEvents");

            migrationBuilder.DropTable(
                name: "CausalSmartMoneyRebuildCheckpoints");
        }
    }
}
