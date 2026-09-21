using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleDiscoveryEvidenceAndAlertLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AvailableTimeMs",
                table: "CandleSequenceSignals",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceKind",
                table: "CandleSequenceSignals",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "CandleSequenceSignals",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "BaselineWinRate",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CapabilityState",
                table: "CandleSequenceRules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "DiscoveryRunId",
                table: "CandleSequenceRules",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EvaluationEndTimeMs",
                table: "CandleSequenceRules",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EvaluationStartTimeMs",
                table: "CandleSequenceRules",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LabelDeadZonePct",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MethodVersion",
                table: "CandleSequenceRules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "OosGrossAvgReturnPct",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OosLift",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OosNetAvgReturnPct",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OosSampleCount",
                table: "CandleSequenceRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "OosWinRate",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedReason",
                table: "CandleSequenceRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RoundTripCostBps",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SelectionEndTimeMs",
                table: "CandleSequenceRules",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SelectionSampleCount",
                table: "CandleSequenceRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "SelectionStartTimeMs",
                table: "CandleSequenceRules",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AvailableTimeMs",
                table: "AppAlerts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAtUtc",
                table: "AppAlerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveryAttemptedAtUtc",
                table: "AppAlerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryError",
                table: "AppAlerts",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryStatus",
                table: "AppAlerts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EvidenceKind",
                table: "AppAlerts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "AppAlerts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RuleDiscoveryRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MethodVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FutureBars = table.Column<int>(type: "integer", nullable: false),
                    CandidateBudget = table.Column<int>(type: "integer", nullable: false),
                    TrialCount = table.Column<int>(type: "integer", nullable: false),
                    LabelDeadZonePct = table.Column<double>(type: "double precision", nullable: false),
                    RoundTripCostBps = table.Column<double>(type: "double precision", nullable: false),
                    SelectionStartTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    SelectionEndTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EvaluationStartTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EvaluationEndTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleDiscoveryRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleDiscoveryTrials",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    TrialNumber = table.Column<int>(type: "integer", nullable: false),
                    CandidateKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ConditionsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RejectedReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SelectionSampleCount = table.Column<int>(type: "integer", nullable: false),
                    SelectionWinRate = table.Column<double>(type: "double precision", nullable: true),
                    SelectionNetAvgReturnPct = table.Column<double>(type: "double precision", nullable: true),
                    EvaluationSampleCount = table.Column<int>(type: "integer", nullable: false),
                    EvaluationWinRate = table.Column<double>(type: "double precision", nullable: true),
                    BaselineWinRate = table.Column<double>(type: "double precision", nullable: true),
                    OosLift = table.Column<double>(type: "double precision", nullable: true),
                    EvaluationNetAvgReturnPct = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleDiscoveryTrials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleDiscoveryTrials_RuleDiscoveryRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "RuleDiscoveryRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                UPDATE "CandleSequenceRules"
                SET "CapabilityState" = 'descriptive',
                    "MethodVersion" = 'legacy-unversioned';

                UPDATE "CandleSequenceSignals"
                SET "AvailableTimeMs" = "TriggerTimeMs",
                    "EvidenceKind" = 'observed-event',
                    "Provenance" = 'legacy-candle-sequence-rule';

                UPDATE "AppAlerts"
                SET "EvidenceKind" = 'observed-event',
                    "Provenance" = 'legacy-alert-record',
                    "DeliveryStatus" = 'historical-db-only';

                DELETE FROM "CandleSequenceSignals" duplicate
                USING "CandleSequenceSignals" canonical
                WHERE duplicate."RuleId" = canonical."RuleId"
                  AND duplicate."Symbol" = canonical."Symbol"
                  AND duplicate."Timeframe" = canonical."Timeframe"
                  AND duplicate."TriggerTimeMs" = canonical."TriggerTimeMs"
                  AND duplicate."Id" > canonical."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CandleSequenceSignals_RuleId_Symbol_Timeframe_TriggerTimeMs",
                table: "CandleSequenceSignals",
                columns: new[] { "RuleId", "Symbol", "Timeframe", "TriggerTimeMs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandleSequenceRules_DiscoveryRunId",
                table: "CandleSequenceRules",
                column: "DiscoveryRunId");

            migrationBuilder.CreateIndex(
                name: "IX_RuleDiscoveryRuns_Symbol_Timeframe_CreatedAtUtc",
                table: "RuleDiscoveryRuns",
                columns: new[] { "Symbol", "Timeframe", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RuleDiscoveryTrials_RunId_TrialNumber",
                table: "RuleDiscoveryTrials",
                columns: new[] { "RunId", "TrialNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuleDiscoveryTrials");

            migrationBuilder.DropTable(
                name: "RuleDiscoveryRuns");

            migrationBuilder.DropIndex(
                name: "IX_CandleSequenceSignals_RuleId_Symbol_Timeframe_TriggerTimeMs",
                table: "CandleSequenceSignals");

            migrationBuilder.DropIndex(
                name: "IX_CandleSequenceRules_DiscoveryRunId",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "AvailableTimeMs",
                table: "CandleSequenceSignals");

            migrationBuilder.DropColumn(
                name: "EvidenceKind",
                table: "CandleSequenceSignals");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "CandleSequenceSignals");

            migrationBuilder.DropColumn(
                name: "BaselineWinRate",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "CapabilityState",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "DiscoveryRunId",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "EvaluationEndTimeMs",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "EvaluationStartTimeMs",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "LabelDeadZonePct",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "MethodVersion",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "OosGrossAvgReturnPct",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "OosLift",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "OosNetAvgReturnPct",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "OosSampleCount",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "OosWinRate",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "RejectedReason",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "RoundTripCostBps",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "SelectionEndTimeMs",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "SelectionSampleCount",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "SelectionStartTimeMs",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "AvailableTimeMs",
                table: "AppAlerts");

            migrationBuilder.DropColumn(
                name: "DeliveredAtUtc",
                table: "AppAlerts");

            migrationBuilder.DropColumn(
                name: "DeliveryAttemptedAtUtc",
                table: "AppAlerts");

            migrationBuilder.DropColumn(
                name: "DeliveryError",
                table: "AppAlerts");

            migrationBuilder.DropColumn(
                name: "DeliveryStatus",
                table: "AppAlerts");

            migrationBuilder.DropColumn(
                name: "EvidenceKind",
                table: "AppAlerts");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "AppAlerts");
        }
    }
}
