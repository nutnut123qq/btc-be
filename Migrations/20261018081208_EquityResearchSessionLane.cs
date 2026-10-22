using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class EquityResearchSessionLane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MbbSignalObservations_FillCoherence",
                table: "MbbSignalObservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MbbSignalObservations_OutcomeCoherence",
                table: "MbbSignalObservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityFundamentals_Metric",
                table: "EquityFundamentals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityForecastRecords_Source",
                table: "EquityForecastRecords");

            migrationBuilder.AddColumn<decimal>(
                name: "BuyBudgetVnd",
                table: "EquityPositionAccounts",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentId",
                table: "EquityFundamentals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceLocation",
                table: "EquityFundamentals",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResearchNoteId",
                table: "EquityForecastRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShareRatio",
                table: "EquityCorporateActions",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EquityCashAccounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AvailableCashVnd = table.Column<decimal>(type: "numeric", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityCashAccounts", x => x.Id);
                    table.CheckConstraint("CK_EquityCashAccounts_Cash", "\"AvailableCashVnd\" IS NULL OR \"AvailableCashVnd\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "EquityResearchNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NoteId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedAsOfUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SignalBarOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    SignalBarCloseTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    HorizonSessions = table.Column<int>(type: "integer", nullable: false),
                    SnapshotId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SkillVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    MethodVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActionView = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActionBasis = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    BodyJson = table.Column<string>(type: "jsonb", nullable: false),
                    AvailableTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityResearchNotes", x => x.Id);
                    table.CheckConstraint("CK_EquityResearchNotes_ActionBasis", "\"ActionView\" = 'none' OR \"ActionBasis\" <> 'insufficient-input'");
                    table.CheckConstraint("CK_EquityResearchNotes_Chronology", "\"SignalBarOpenTimeMs\" < \"SignalBarCloseTimeMs\" AND (\"Mode\" <> 'forward' OR \"AvailableTimeMs\" >= \"SignalBarCloseTimeMs\")");
                    table.CheckConstraint("CK_EquityResearchNotes_Enums", "\"Direction\" IN ('up','down','sideways','no-view') AND \"ActionView\" IN ('buy-more','hold','reduce','sell','wait','none') AND \"ActionBasis\" IN ('position-aware','market-only','insufficient-input')");
                    table.CheckConstraint("CK_EquityResearchNotes_Provenance", "\"SnapshotSha256\" ~ '^[0-9a-f]{64}$' AND NULLIF(BTRIM(\"SnapshotId\"), '') IS NOT NULL AND NULLIF(BTRIM(\"SkillVersion\"), '') IS NOT NULL AND NULLIF(BTRIM(\"MethodVersion\"), '') IS NOT NULL");
                    table.CheckConstraint("CK_EquityResearchNotes_Scope", "\"Symbol\" IN ('MBB','BID') AND \"Mode\" IN ('forward','retrospective') AND \"HorizonSessions\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "EquitySourceDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Entity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ByteSize = table.Column<long>(type: "bigint", nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReportingPeriod = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Consolidation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AuditStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SupersedesDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExtractionJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquitySourceDocuments", x => x.Id);
                    table.CheckConstraint("CK_EquitySourceDocuments_Enums", "\"Consolidation\" IN ('consolidated','separate','unknown') AND \"AuditStatus\" IN ('audited','reviewed','unaudited','unknown')");
                    table.CheckConstraint("CK_EquitySourceDocuments_Period", "\"ReportingPeriod\" IS NULL OR \"ReportingPeriod\" ~ '^[0-9]{4}(Q[1-4])?$'");
                    table.CheckConstraint("CK_EquitySourceDocuments_Provenance", "\"SourceUrl\" ~ '^https?://' AND \"ContentSha256\" ~ '^[0-9a-f]{64}$' AND NULLIF(BTRIM(\"StoragePath\"), '') IS NOT NULL AND NULLIF(BTRIM(\"Provider\"), '') IS NOT NULL AND \"ByteSize\" > 0 AND \"FetchedAtUtc\" >= \"FirstSeenAtUtc\"");
                    table.CheckConstraint("CK_EquitySourceDocuments_Type", "\"DocType\" IN ('financial-statement','investor-presentation','disclosure','annual-report','other')");
                });

            migrationBuilder.CreateTable(
                name: "MarketCalendarDays",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Venue = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Verification = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CalendarVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketCalendarDays", x => x.Id);
                    table.CheckConstraint("CK_MarketCalendarDays_Kind", "\"Kind\" IN ('market-holiday','market-closure')");
                    table.CheckConstraint("CK_MarketCalendarDays_Verification", "(\"Verification\" = 'verified' AND \"SourceUrl\" ~ '^https?://') OR \"Verification\" = 'unverified-legacy'");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MbbSignalObservations_FillCoherence",
                table: "MbbSignalObservations",
                sql: "(\"FillPrice\" IS NULL AND \"FillObservedAtUtc\" IS NULL AND \"FillSource\" IS NULL AND \"FillBarOpenTimeMs\" IS NULL) OR (\"FillPrice\" IS NOT NULL AND \"FillObservedAtUtc\" IS NOT NULL AND \"FillBarOpenTimeMs\" IS NOT NULL AND \"Decision\" IN ('buy-more','reduce','sell','conditional-sell') AND \"FillPrice\" > 0 AND \"FillObservedAtUtc\" >= \"ObservedAtUtc\" AND NULLIF(BTRIM(\"FillSource\"), '') IS NOT NULL AND \"FillBarOpenTimeMs\" >= \"AvailableTimeMs\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MbbSignalObservations_OutcomeCoherence",
                table: "MbbSignalObservations",
                sql: "(\"OutcomeReturn\" IS NULL AND \"OutcomeObservedAtUtc\" IS NULL AND \"OutcomeHorizon\" IS NULL) OR (\"FillPrice\" IS NOT NULL AND \"FillObservedAtUtc\" IS NOT NULL AND \"OutcomeReturn\" IS NOT NULL AND \"OutcomeObservedAtUtc\" IS NOT NULL AND \"OutcomeObservedAtUtc\" >= \"FillObservedAtUtc\" AND NULLIF(BTRIM(\"OutcomeHorizon\"), '') IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_EquityFundamentals_DocumentId",
                table: "EquityFundamentals",
                column: "DocumentId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityFundamentals_Metric",
                table: "EquityFundamentals",
                sql: "\"Metric\" IN ('BVPS','PB','ROE','NIM','NPL','COVERAGE','LLR','CAR','LDR','TOTAL_ASSETS','NET_PROFIT','EQUITY')");

            migrationBuilder.CreateIndex(
                name: "IX_EquityForecastRecords_ResearchNoteId",
                table: "EquityForecastRecords",
                column: "ResearchNoteId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityForecastRecords_SessionUnvalidated",
                table: "EquityForecastRecords",
                sql: "\"Source\" <> 'session-research' OR (NOT \"Validated\" AND \"ResearchNoteId\" IS NOT NULL AND NULLIF(BTRIM(\"MethodVersion\"), '') IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityForecastRecords_Source",
                table: "EquityForecastRecords",
                sql: "\"Source\" IN ('quant-knn','llm-judge','session-research')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityCorporateActions_ShareRatio",
                table: "EquityCorporateActions",
                sql: "\"ShareRatio\" IS NULL OR \"ShareRatio\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_EquityCashAccounts_AccountId",
                table: "EquityCashAccounts",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityResearchNotes_NoteId",
                table: "EquityResearchNotes",
                column: "NoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityResearchNotes_Symbol_SignalBarOpenTimeMs",
                table: "EquityResearchNotes",
                columns: new[] { "Symbol", "SignalBarOpenTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_EquitySourceDocuments_Symbol_ContentSha256",
                table: "EquitySourceDocuments",
                columns: new[] { "Symbol", "ContentSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquitySourceDocuments_Symbol_ReportingPeriod",
                table: "EquitySourceDocuments",
                columns: new[] { "Symbol", "ReportingPeriod" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketCalendarDays_Venue_Date",
                table: "MarketCalendarDays",
                columns: new[] { "Venue", "Date" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_EquityFundamentals_EquitySourceDocuments_DocumentId",
                table: "EquityFundamentals",
                column: "DocumentId",
                principalTable: "EquitySourceDocuments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // EQX v2 A2: database-enforced write-once for the forward journals (all writers, EF and raw SQL).
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION ""fn_mbb_signal_observations_write_once""() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN
  IF (NEW.""Id"", NEW.""DecisionId"", NEW.""RecorderVersion"", NEW.""Symbol"", NEW.""Timeframe"", NEW.""SignalBarOpenTimeMs"", NEW.""SignalBarCloseTimeMs"", NEW.""ObservedAtUtc"", NEW.""AvailableTimeMs"", NEW.""Decision"", NEW.""RulesVersion"", NEW.""EvidenceStatus"", NEW.""PriceBasis"", NEW.""MarketCloseVnd"", NEW.""DecisionPayloadJson"", NEW.""CreatedAtUtc"") IS DISTINCT FROM (OLD.""Id"", OLD.""DecisionId"", OLD.""RecorderVersion"", OLD.""Symbol"", OLD.""Timeframe"", OLD.""SignalBarOpenTimeMs"", OLD.""SignalBarCloseTimeMs"", OLD.""ObservedAtUtc"", OLD.""AvailableTimeMs"", OLD.""Decision"", OLD.""RulesVersion"", OLD.""EvidenceStatus"", OLD.""PriceBasis"", OLD.""MarketCloseVnd"", OLD.""DecisionPayloadJson"", OLD.""CreatedAtUtc"") THEN RAISE EXCEPTION 'immutable columns of MbbSignalObservations cannot change after insert' USING ERRCODE = '23514'; END IF;
  IF OLD.""FillPrice"" IS NOT NULL AND NEW.""FillPrice"" IS DISTINCT FROM OLD.""FillPrice"" THEN RAISE EXCEPTION 'write-once column FillPrice already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""FillObservedAtUtc"" IS NOT NULL AND NEW.""FillObservedAtUtc"" IS DISTINCT FROM OLD.""FillObservedAtUtc"" THEN RAISE EXCEPTION 'write-once column FillObservedAtUtc already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""FillSource"" IS NOT NULL AND NEW.""FillSource"" IS DISTINCT FROM OLD.""FillSource"" THEN RAISE EXCEPTION 'write-once column FillSource already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""FillBarOpenTimeMs"" IS NOT NULL AND NEW.""FillBarOpenTimeMs"" IS DISTINCT FROM OLD.""FillBarOpenTimeMs"" THEN RAISE EXCEPTION 'write-once column FillBarOpenTimeMs already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""OutcomeReturn"" IS NOT NULL AND NEW.""OutcomeReturn"" IS DISTINCT FROM OLD.""OutcomeReturn"" THEN RAISE EXCEPTION 'write-once column OutcomeReturn already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""OutcomeObservedAtUtc"" IS NOT NULL AND NEW.""OutcomeObservedAtUtc"" IS DISTINCT FROM OLD.""OutcomeObservedAtUtc"" THEN RAISE EXCEPTION 'write-once column OutcomeObservedAtUtc already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""OutcomeHorizon"" IS NOT NULL AND NEW.""OutcomeHorizon"" IS DISTINCT FROM OLD.""OutcomeHorizon"" THEN RAISE EXCEPTION 'write-once column OutcomeHorizon already set' USING ERRCODE = '23514'; END IF;
  RETURN NEW;
END
$fn$;
DROP TRIGGER IF EXISTS ""TR_MbbSignalObservations_WriteOnce"" ON ""MbbSignalObservations"";
CREATE TRIGGER ""TR_MbbSignalObservations_WriteOnce"" BEFORE UPDATE ON ""MbbSignalObservations"" FOR EACH ROW EXECUTE FUNCTION ""fn_mbb_signal_observations_write_once""();");
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION ""fn_equity_forecast_records_write_once""() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN
  IF (NEW.""Id"", NEW.""ForecastId"", NEW.""Source"", NEW.""Symbol"", NEW.""Timeframe"", NEW.""ConfigId"", NEW.""TargetPct"", NEW.""StopPct"", NEW.""HorizonSessions"", NEW.""SignalBarOpenTimeMs"", NEW.""SignalBarCloseTimeMs"", NEW.""AvailableTimeMs"", NEW.""CreatedAtUtc"", NEW.""ProbTargetFirst"", NEW.""ProbStopFirst"", NEW.""Abstained"", NEW.""AbstainReason"", NEW.""Validated"", NEW.""MethodVersion"", NEW.""ModelId"", NEW.""PromptVersion"", NEW.""SchemaVersion"", NEW.""InputSnapshotSha256"", NEW.""LlmRunId"", NEW.""ResearchNoteId"") IS DISTINCT FROM (OLD.""Id"", OLD.""ForecastId"", OLD.""Source"", OLD.""Symbol"", OLD.""Timeframe"", OLD.""ConfigId"", OLD.""TargetPct"", OLD.""StopPct"", OLD.""HorizonSessions"", OLD.""SignalBarOpenTimeMs"", OLD.""SignalBarCloseTimeMs"", OLD.""AvailableTimeMs"", OLD.""CreatedAtUtc"", OLD.""ProbTargetFirst"", OLD.""ProbStopFirst"", OLD.""Abstained"", OLD.""AbstainReason"", OLD.""Validated"", OLD.""MethodVersion"", OLD.""ModelId"", OLD.""PromptVersion"", OLD.""SchemaVersion"", OLD.""InputSnapshotSha256"", OLD.""LlmRunId"", OLD.""ResearchNoteId"") THEN RAISE EXCEPTION 'immutable columns of EquityForecastRecords cannot change after insert' USING ERRCODE = '23514'; END IF;
  IF OLD.""EntryBarOpenTimeMs"" IS NOT NULL AND NEW.""EntryBarOpenTimeMs"" IS DISTINCT FROM OLD.""EntryBarOpenTimeMs"" THEN RAISE EXCEPTION 'write-once column EntryBarOpenTimeMs already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""EntryPrice"" IS NOT NULL AND NEW.""EntryPrice"" IS DISTINCT FROM OLD.""EntryPrice"" THEN RAISE EXCEPTION 'write-once column EntryPrice already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""OutcomeLabel"" IS NOT NULL AND NEW.""OutcomeLabel"" IS DISTINCT FROM OLD.""OutcomeLabel"" THEN RAISE EXCEPTION 'write-once column OutcomeLabel already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""OutcomeReturnNet"" IS NOT NULL AND NEW.""OutcomeReturnNet"" IS DISTINCT FROM OLD.""OutcomeReturnNet"" THEN RAISE EXCEPTION 'write-once column OutcomeReturnNet already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""ResolvedBarOpenTimeMs"" IS NOT NULL AND NEW.""ResolvedBarOpenTimeMs"" IS DISTINCT FROM OLD.""ResolvedBarOpenTimeMs"" THEN RAISE EXCEPTION 'write-once column ResolvedBarOpenTimeMs already set' USING ERRCODE = '23514'; END IF;
  IF OLD.""OutcomeResolvedAtUtc"" IS NOT NULL AND NEW.""OutcomeResolvedAtUtc"" IS DISTINCT FROM OLD.""OutcomeResolvedAtUtc"" THEN RAISE EXCEPTION 'write-once column OutcomeResolvedAtUtc already set' USING ERRCODE = '23514'; END IF;
  RETURN NEW;
END
$fn$;
DROP TRIGGER IF EXISTS ""TR_EquityForecastRecords_WriteOnce"" ON ""EquityForecastRecords"";
CREATE TRIGGER ""TR_EquityForecastRecords_WriteOnce"" BEFORE UPDATE ON ""EquityForecastRecords"" FOR EACH ROW EXECUTE FUNCTION ""fn_equity_forecast_records_write_once""();");
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION ""fn_equity_research_notes_immutable""() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN RAISE EXCEPTION 'EquityResearchNotes rows are immutable after insert' USING ERRCODE = '23514'; END
$fn$;
DROP TRIGGER IF EXISTS ""TR_EquityResearchNotes_Immutable"" ON ""EquityResearchNotes"";
CREATE TRIGGER ""TR_EquityResearchNotes_Immutable"" BEFORE UPDATE ON ""EquityResearchNotes"" FOR EACH ROW EXECUTE FUNCTION ""fn_equity_research_notes_immutable""();");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_EquityResearchNotes_Immutable"" ON ""EquityResearchNotes""; DROP FUNCTION IF EXISTS ""fn_equity_research_notes_immutable""();");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_EquityForecastRecords_WriteOnce"" ON ""EquityForecastRecords""; DROP FUNCTION IF EXISTS ""fn_equity_forecast_records_write_once""();");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_MbbSignalObservations_WriteOnce"" ON ""MbbSignalObservations""; DROP FUNCTION IF EXISTS ""fn_mbb_signal_observations_write_once""();");

            migrationBuilder.DropForeignKey(
                name: "FK_EquityFundamentals_EquitySourceDocuments_DocumentId",
                table: "EquityFundamentals");

            migrationBuilder.DropTable(
                name: "EquityCashAccounts");

            migrationBuilder.DropTable(
                name: "EquityResearchNotes");

            migrationBuilder.DropTable(
                name: "EquitySourceDocuments");

            migrationBuilder.DropTable(
                name: "MarketCalendarDays");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MbbSignalObservations_FillCoherence",
                table: "MbbSignalObservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MbbSignalObservations_OutcomeCoherence",
                table: "MbbSignalObservations");

            migrationBuilder.DropIndex(
                name: "IX_EquityFundamentals_DocumentId",
                table: "EquityFundamentals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityFundamentals_Metric",
                table: "EquityFundamentals");

            migrationBuilder.DropIndex(
                name: "IX_EquityForecastRecords_ResearchNoteId",
                table: "EquityForecastRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityForecastRecords_SessionUnvalidated",
                table: "EquityForecastRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityForecastRecords_Source",
                table: "EquityForecastRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityCorporateActions_ShareRatio",
                table: "EquityCorporateActions");

            migrationBuilder.DropColumn(
                name: "BuyBudgetVnd",
                table: "EquityPositionAccounts");

            migrationBuilder.DropColumn(
                name: "DocumentId",
                table: "EquityFundamentals");

            migrationBuilder.DropColumn(
                name: "SourceLocation",
                table: "EquityFundamentals");

            migrationBuilder.DropColumn(
                name: "ResearchNoteId",
                table: "EquityForecastRecords");

            migrationBuilder.DropColumn(
                name: "ShareRatio",
                table: "EquityCorporateActions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MbbSignalObservations_FillCoherence",
                table: "MbbSignalObservations",
                sql: "(\"FillPrice\" IS NULL AND \"FillObservedAtUtc\" IS NULL AND \"FillSource\" IS NULL AND \"FillBarOpenTimeMs\" IS NULL) OR (\"Decision\" IN ('buy-more','reduce','sell','conditional-sell') AND \"FillPrice\" > 0 AND \"FillObservedAtUtc\" >= \"ObservedAtUtc\" AND NULLIF(BTRIM(\"FillSource\"), '') IS NOT NULL AND \"FillBarOpenTimeMs\" >= \"AvailableTimeMs\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MbbSignalObservations_OutcomeCoherence",
                table: "MbbSignalObservations",
                sql: "(\"OutcomeReturn\" IS NULL AND \"OutcomeObservedAtUtc\" IS NULL AND \"OutcomeHorizon\" IS NULL) OR (\"FillPrice\" IS NOT NULL AND \"OutcomeReturn\" IS NOT NULL AND \"OutcomeObservedAtUtc\" >= \"FillObservedAtUtc\" AND NULLIF(BTRIM(\"OutcomeHorizon\"), '') IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityFundamentals_Metric",
                table: "EquityFundamentals",
                sql: "\"Metric\" IN ('BVPS','PB','ROE','NIM','NPL','COVERAGE','LLR','CAR','LDR')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityForecastRecords_Source",
                table: "EquityForecastRecords",
                sql: "\"Source\" IN ('quant-knn','llm-judge')");
        }
    }
}
