using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddResearchValidityAndAlertDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                table: "ModelPredictions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationVersion",
                table: "ModelPredictions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "legacy-unversioned");

            migrationBuilder.AddColumn<string>(
                name: "InvalidReason",
                table: "ModelPredictions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PipelineVersion",
                table: "ModelPredictions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "legacy-unversioned");

            migrationBuilder.AddColumn<string>(
                name: "ValidityStatus",
                table: "ModelPredictions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                table: "EnsemblePredictionRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationVersion",
                table: "EnsemblePredictionRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "legacy-unversioned");

            migrationBuilder.AddColumn<string>(
                name: "InvalidReason",
                table: "EnsemblePredictionRecords",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PipelineVersion",
                table: "EnsemblePredictionRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "legacy-unversioned");

            migrationBuilder.AddColumn<string>(
                name: "ValidityStatus",
                table: "EnsemblePredictionRecords",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                table: "BacktestRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationVersion",
                table: "BacktestRuns",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "legacy-unversioned");

            migrationBuilder.AddColumn<string>(
                name: "InvalidReason",
                table: "BacktestRuns",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PipelineVersion",
                table: "BacktestRuns",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "legacy-unversioned");

            migrationBuilder.AddColumn<string>(
                name: "ValidityStatus",
                table: "BacktestRuns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                table: "AppAlerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "AppAlerts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "BacktestRuns"
                SET "InvalidReason" = 'MISSING_VERSION_METADATA: Record predates versioned pipeline/evaluation evidence.';

                UPDATE "ModelPredictions"
                SET "InvalidReason" = 'MISSING_VERSION_METADATA: Prediction predates versioned pipeline/evaluation evidence.';

                UPDATE "EnsemblePredictionRecords"
                SET "InvalidReason" = 'MISSING_VERSION_METADATA: Ensemble record predates point-in-time versioned evaluation.';

                WITH matches AS (
                    SELECT
                        a."Id" AS alert_id,
                        s."RuleId" AS rule_id,
                        s."Symbol" AS symbol,
                        s."Timeframe" AS timeframe,
                        s."TriggerTimeMs" AS trigger_time_ms,
                        COUNT(*) OVER (PARTITION BY a."Id") AS match_count,
                        ROW_NUMBER() OVER (
                            PARTITION BY a."Id"
                            ORDER BY ABS(EXTRACT(EPOCH FROM (a."CreatedAt" - s."CreatedAtUtc"))), s."Id"
                        ) AS match_rank
                    FROM "AppAlerts" a
                    JOIN "CandleSequenceRules" r
                      ON r."Name" = a."Title"
                    JOIN "CandleSequenceSignals" s
                      ON s."RuleId" = r."Id"
                     AND s."Message" = a."Message"
                     AND s."ClosePrice" = a."PriceSnapshot"
                     AND ABS(EXTRACT(EPOCH FROM (a."CreatedAt" - s."CreatedAtUtc"))) <= 5
                    WHERE a."Type" = 'sequence_rule'
                      AND a."SourceKey" IS NULL
                )
                UPDATE "AppAlerts" a
                SET "SourceKey" = CONCAT(
                    'sequence:', BTRIM(a."UserId"), ':', m.rule_id, ':',
                    UPPER(BTRIM(m.symbol)), ':', LOWER(BTRIM(m.timeframe)), ':', m.trigger_time_ms
                )
                FROM matches m
                WHERE a."Id" = m.alert_id
                  AND m.match_count = 1
                  AND m.match_rank = 1;

                WITH ranked AS (
                    SELECT
                        "Id",
                        ROW_NUMBER() OVER (
                            PARTITION BY "UserId", "SourceKey"
                            ORDER BY "CreatedAt", "Id"
                        ) AS duplicate_rank
                    FROM "AppAlerts"
                    WHERE "SourceKey" IS NOT NULL
                      AND "ArchivedAtUtc" IS NULL
                )
                UPDATE "AppAlerts" a
                SET "ArchivedAtUtc" = CURRENT_TIMESTAMP
                FROM ranked r
                WHERE a."Id" = r."Id"
                  AND r.duplicate_rank > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ModelPredictions_ValidityStatus_ArchivedAtUtc",
                table: "ModelPredictions",
                columns: new[] { "ValidityStatus", "ArchivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EnsemblePredictionRecords_ValidityStatus_ArchivedAtUtc",
                table: "EnsemblePredictionRecords",
                columns: new[] { "ValidityStatus", "ArchivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_ValidityStatus_ArchivedAtUtc",
                table: "BacktestRuns",
                columns: new[] { "ValidityStatus", "ArchivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AppAlerts_UserId_SourceKey",
                table: "AppAlerts",
                columns: new[] { "UserId", "SourceKey" },
                unique: true,
                filter: "\"SourceKey\" IS NOT NULL AND \"ArchivedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ModelPredictions_ValidityStatus_ArchivedAtUtc",
                table: "ModelPredictions");

            migrationBuilder.DropIndex(
                name: "IX_EnsemblePredictionRecords_ValidityStatus_ArchivedAtUtc",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRuns_ValidityStatus_ArchivedAtUtc",
                table: "BacktestRuns");

            migrationBuilder.DropIndex(
                name: "IX_AppAlerts_UserId_SourceKey",
                table: "AppAlerts");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "ModelPredictions");

            migrationBuilder.DropColumn(
                name: "EvaluationVersion",
                table: "ModelPredictions");

            migrationBuilder.DropColumn(
                name: "InvalidReason",
                table: "ModelPredictions");

            migrationBuilder.DropColumn(
                name: "PipelineVersion",
                table: "ModelPredictions");

            migrationBuilder.DropColumn(
                name: "ValidityStatus",
                table: "ModelPredictions");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "EvaluationVersion",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "InvalidReason",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "PipelineVersion",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "ValidityStatus",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "EvaluationVersion",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "InvalidReason",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "PipelineVersion",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "ValidityStatus",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "AppAlerts");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "AppAlerts");
        }
    }
}
