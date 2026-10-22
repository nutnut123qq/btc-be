using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class EquityExpansionLane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MbbSignalObservations_Scope",
                table: "MbbSignalObservations");

            migrationBuilder.CreateTable(
                name: "EquityCorporateActions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AnnouncedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ActionType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RatioText = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CashPerShareVnd = table.Column<decimal>(type: "numeric", nullable: true),
                    AdjustmentFactor = table.Column<decimal>(type: "numeric", nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Provenance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerificationStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityCorporateActions", x => x.Id);
                    table.CheckConstraint("CK_EquityCorporateActions_Factor", "\"AdjustmentFactor\" IS NULL OR \"AdjustmentFactor\" > 0");
                    table.CheckConstraint("CK_EquityCorporateActions_Source", "\"SourceUrl\" ~ '^https?://' AND NULLIF(BTRIM(\"Provenance\"), '') IS NOT NULL");
                    table.CheckConstraint("CK_EquityCorporateActions_Type", "\"ActionType\" IN ('cash-dividend','stock-dividend','bonus-shares','rights-issue','split','other')");
                    table.CheckConstraint("CK_EquityCorporateActions_Verification", "\"VerificationStatus\" IN ('unverified','verified','rejected')");
                });

            migrationBuilder.CreateTable(
                name: "EquityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EventDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Provenance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityEvents", x => x.Id);
                    table.CheckConstraint("CK_EquityEvents_Source", "\"SourceUrl\" ~ '^https?://' AND NULLIF(BTRIM(\"Provenance\"), '') IS NOT NULL");
                    table.CheckConstraint("CK_EquityEvents_Type", "\"EventType\" IN ('earnings','agm','additional-listing','other')");
                });

            migrationBuilder.CreateTable(
                name: "EquityForecastRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ForecastId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConfigId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetPct = table.Column<double>(type: "double precision", nullable: false),
                    StopPct = table.Column<double>(type: "double precision", nullable: false),
                    HorizonSessions = table.Column<int>(type: "integer", nullable: false),
                    SignalBarOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    SignalBarCloseTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    AvailableTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProbTargetFirst = table.Column<double>(type: "double precision", nullable: true),
                    ProbStopFirst = table.Column<double>(type: "double precision", nullable: true),
                    Abstained = table.Column<bool>(type: "boolean", nullable: false),
                    AbstainReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Validated = table.Column<bool>(type: "boolean", nullable: false),
                    MethodVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SchemaVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    InputSnapshotSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LlmRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntryBarOpenTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    OutcomeLabel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    OutcomeReturnNet = table.Column<double>(type: "double precision", nullable: true),
                    ResolvedBarOpenTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    OutcomeResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityForecastRecords", x => x.Id);
                    table.CheckConstraint("CK_EquityForecastRecords_Abstention", "NOT \"Abstained\" OR (\"ProbTargetFirst\" IS NULL AND \"ProbStopFirst\" IS NULL AND NULLIF(BTRIM(\"AbstainReason\"), '') IS NOT NULL)");
                    table.CheckConstraint("CK_EquityForecastRecords_Chronology", "\"SignalBarOpenTimeMs\" < \"SignalBarCloseTimeMs\" AND \"AvailableTimeMs\" >= \"SignalBarCloseTimeMs\"");
                    table.CheckConstraint("CK_EquityForecastRecords_LlmUnvalidated", "\"Source\" <> 'llm-judge' OR (NOT \"Validated\" AND \"LlmRunId\" IS NOT NULL)");
                    table.CheckConstraint("CK_EquityForecastRecords_OutcomeCoherence", "(\"EntryBarOpenTimeMs\" IS NULL AND \"EntryPrice\" IS NULL AND \"OutcomeLabel\" IS NULL AND \"OutcomeReturnNet\" IS NULL AND \"ResolvedBarOpenTimeMs\" IS NULL AND \"OutcomeResolvedAtUtc\" IS NULL) OR (\"OutcomeLabel\" IN ('target-first','stop-first','timeout') AND \"EntryPrice\" > 0 AND \"OutcomeReturnNet\" IS NOT NULL AND \"EntryBarOpenTimeMs\" > \"SignalBarOpenTimeMs\" AND \"EntryBarOpenTimeMs\" >= \"AvailableTimeMs\" AND \"ResolvedBarOpenTimeMs\" >= \"EntryBarOpenTimeMs\" AND \"OutcomeResolvedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_EquityForecastRecords_Probability", "(\"ProbTargetFirst\" IS NULL OR (\"ProbTargetFirst\" >= 0 AND \"ProbTargetFirst\" <= 1)) AND (\"ProbStopFirst\" IS NULL OR (\"ProbStopFirst\" >= 0 AND \"ProbStopFirst\" <= 1)) AND (\"ProbTargetFirst\" IS NULL OR \"ProbStopFirst\" IS NULL OR \"ProbTargetFirst\" + \"ProbStopFirst\" <= 1.000001)");
                    table.CheckConstraint("CK_EquityForecastRecords_Scope", "\"Symbol\" IN ('MBB','BID') AND \"Timeframe\" = '1d' AND \"HorizonSessions\" > 0");
                    table.CheckConstraint("CK_EquityForecastRecords_Source", "\"Source\" IN ('quant-knn','llm-judge')");
                });

            migrationBuilder.CreateTable(
                name: "EquityFundamentals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Period = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    PeriodEndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Metric = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Value = table.Column<decimal>(type: "numeric", nullable: true),
                    Unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UnavailableReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PublishedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AsOfUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Provenance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityFundamentals", x => x.Id);
                    table.CheckConstraint("CK_EquityFundamentals_Metric", "\"Metric\" IN ('BVPS','PB','ROE','NIM','NPL','COVERAGE','LLR','CAR','LDR')");
                    table.CheckConstraint("CK_EquityFundamentals_Period", "\"Period\" ~ '^[0-9]{4}Q[1-4]$'");
                    table.CheckConstraint("CK_EquityFundamentals_Source", "\"SourceUrl\" ~ '^https?://' AND NULLIF(BTRIM(\"Provenance\"), '') IS NOT NULL");
                    table.CheckConstraint("CK_EquityFundamentals_Unit", "\"Unit\" IN ('VND','ratio','percent')");
                    table.CheckConstraint("CK_EquityFundamentals_ValueOrReason", "\"Value\" IS NOT NULL OR NULLIF(BTRIM(\"UnavailableReason\"), '') IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "EquityLlmRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SignalBarOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ResearchModel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    JudgeModel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    InputSnapshotSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    OutputJson = table.Column<string>(type: "jsonb", nullable: true),
                    UsageJson = table.Column<string>(type: "jsonb", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityLlmRuns", x => x.Id);
                    table.CheckConstraint("CK_EquityLlmRuns_Output", "\"Status\" <> 'succeeded' OR \"OutputJson\" IS NOT NULL");
                    table.CheckConstraint("CK_EquityLlmRuns_Scope", "\"Symbol\" IN ('MBB','BID')");
                    table.CheckConstraint("CK_EquityLlmRuns_Status", "\"Status\" IN ('succeeded','failed','unavailable')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MbbSignalObservations_Scope",
                table: "MbbSignalObservations",
                sql: "(\"Symbol\" = 'MBB' AND \"Timeframe\" IN ('1h','1d')) OR (\"Symbol\" = 'BID' AND \"Timeframe\" = '1d')");

            migrationBuilder.CreateIndex(
                name: "IX_EquityCorporateActions_Symbol_ExDate_ActionType",
                table: "EquityCorporateActions",
                columns: new[] { "Symbol", "ExDate", "ActionType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityEvents_Symbol_EventType_EventDate_Title",
                table: "EquityEvents",
                columns: new[] { "Symbol", "EventType", "EventDate", "Title" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityForecastRecords_ForecastId",
                table: "EquityForecastRecords",
                column: "ForecastId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityForecastRecords_Symbol_ConfigId_SignalBarOpenTimeMs",
                table: "EquityForecastRecords",
                columns: new[] { "Symbol", "ConfigId", "SignalBarOpenTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_EquityFundamentals_Symbol_Period_Metric_Source",
                table: "EquityFundamentals",
                columns: new[] { "Symbol", "Period", "Metric", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityLlmRuns_Symbol_CreatedAtUtc",
                table: "EquityLlmRuns",
                columns: new[] { "Symbol", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EquityCorporateActions");

            migrationBuilder.DropTable(
                name: "EquityEvents");

            migrationBuilder.DropTable(
                name: "EquityForecastRecords");

            migrationBuilder.DropTable(
                name: "EquityFundamentals");

            migrationBuilder.DropTable(
                name: "EquityLlmRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MbbSignalObservations_Scope",
                table: "MbbSignalObservations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MbbSignalObservations_Scope",
                table: "MbbSignalObservations",
                sql: "\"Symbol\" = 'MBB' AND \"Timeframe\" IN ('1h','1d')");
        }
    }
}
