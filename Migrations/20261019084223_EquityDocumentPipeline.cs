using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class EquityDocumentPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EquitySourceDocuments_Period",
                table: "EquitySourceDocuments");

            migrationBuilder.CreateTable(
                name: "EquityDocumentExtractions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtractorVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PageCount = table.Column<int>(type: "integer", nullable: false),
                    PagesOcr = table.Column<int>(type: "integer", nullable: false),
                    ArtifactPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    QualityJson = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityDocumentExtractions", x => x.Id);
                    table.CheckConstraint("CK_EquityDocumentExtractions_Enums", "\"Method\" IN ('text-layer','ocr','mixed') AND \"Status\" IN ('succeeded','partial','failed')");
                    table.CheckConstraint("CK_EquityDocumentExtractions_Pages", "\"PageCount\" >= 0 AND \"PagesOcr\" >= 0 AND \"PagesOcr\" <= \"PageCount\" AND \"FinishedAtUtc\" >= \"StartedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_EquityDocumentExtractions_EquitySourceDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "EquitySourceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EquityPipelineRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HeartbeatAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CountsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CheckpointJson = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LogTail = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityPipelineRuns", x => x.Id);
                    table.CheckConstraint("CK_EquityPipelineRuns_Chronology", "\"HeartbeatAtUtc\" >= \"StartedAtUtc\" AND (\"FinishedAtUtc\" IS NULL OR \"FinishedAtUtc\" >= \"StartedAtUtc\") AND ((\"Status\" = 'running') = (\"FinishedAtUtc\" IS NULL))");
                    table.CheckConstraint("CK_EquityPipelineRuns_Enums", "\"Mode\" IN ('backfill','incremental') AND \"Trigger\" IN ('worker','admin','cli') AND \"Status\" IN ('running','succeeded','partial','failed','cancelled') AND \"Symbol\" IN ('MBB','BID')");
                });

            migrationBuilder.CreateTable(
                name: "EquitySourceAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetPeriod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DocType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Consolidation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AttemptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquitySourceAttempts", x => x.Id);
                    table.CheckConstraint("CK_EquitySourceAttempts_Outcome", "\"Outcome\" IN ('listed','not-listed','downloaded','unchanged','changed','http-error','rate-limited','timeout','parse-error','budget-exhausted','too-large')");
                    table.CheckConstraint("CK_EquitySourceAttempts_Scope", "\"Symbol\" IN ('MBB','BID')");
                });

            migrationBuilder.CreateTable(
                name: "EquitySourceDocumentLocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ListingTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ListingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HttpEtag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HttpLastModified = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquitySourceDocumentLocations", x => x.Id);
                    table.CheckConstraint("CK_EquitySourceDocumentLocations_Url", "\"SourceUrl\" ~ '^https?://' AND \"LastSeenAtUtc\" >= \"FirstSeenAtUtc\"");
                    table.ForeignKey(
                        name: "FK_EquitySourceDocumentLocations_EquitySourceDocuments_Documen~",
                        column: x => x.DocumentId,
                        principalTable: "EquitySourceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EquityFinancialFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MetricCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetricDefinitionVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MetricGroup = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Statement = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PeriodLabel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PeriodType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    FiscalYear = table.Column<int>(type: "integer", nullable: false),
                    Consolidation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Value = table.Column<decimal>(type: "numeric", nullable: false),
                    Unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Scale = table.Column<decimal>(type: "numeric", nullable: false),
                    RawLabel = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RawValueText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RawUnitText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ColumnRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: true),
                    Location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtractionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ValidationJson = table.Column<string>(type: "jsonb", nullable: true),
                    IsDerived = table.Column<bool>(type: "boolean", nullable: false),
                    DerivationJson = table.Column<string>(type: "jsonb", nullable: true),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityFinancialFacts", x => x.Id);
                    table.CheckConstraint("CK_EquityFinancialFacts_Enums", "\"MetricGroup\" IN ('balance-sheet','income-statement','cash-flow','credit-quality','deposits-funding','capital','shares') AND \"Statement\" IN ('balance-sheet','income-statement','cash-flow','note','derived') AND \"PeriodType\" IN ('quarter','ytd','half-year','annual','point-in-time') AND \"Consolidation\" IN ('consolidated','separate') AND \"Unit\" IN ('VND','percent','shares','count','ratio') AND \"ColumnRole\" IN ('current','comparative','cumulative-current','cumulative-comparative','derived') AND \"Status\" IN ('extracted','validated','review-needed','quarantined')");
                    table.CheckConstraint("CK_EquityFinancialFacts_Period", "\"PeriodLabel\" ~ '^[0-9]{4}(Q[1-4]|H[12]|-9M|-6M|-3M)?$' AND (\"PeriodStart\" IS NULL OR \"PeriodStart\" <= \"PeriodEnd\") AND \"Scale\" > 0");
                    table.CheckConstraint("CK_EquityFinancialFacts_Provenance", "(\"IsDerived\" AND \"DerivationJson\" IS NOT NULL AND \"ExtractionId\" IS NULL AND \"ColumnRole\" = 'derived') OR (NOT \"IsDerived\" AND \"DerivationJson\" IS NULL AND \"ExtractionId\" IS NOT NULL AND \"PageNumber\" > 0 AND NULLIF(BTRIM(\"RawValueText\"), '') IS NOT NULL AND \"ColumnRole\" <> 'derived')");
                    table.CheckConstraint("CK_EquityFinancialFacts_Scope", "\"Symbol\" IN ('MBB','BID')");
                    table.ForeignKey(
                        name: "FK_EquityFinancialFacts_EquityDocumentExtractions_ExtractionId",
                        column: x => x.ExtractionId,
                        principalTable: "EquityDocumentExtractions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EquityFinancialFacts_EquitySourceDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "EquitySourceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquitySourceDocuments_Period",
                table: "EquitySourceDocuments",
                sql: "\"ReportingPeriod\" IS NULL OR \"ReportingPeriod\" ~ '^[0-9]{4}(Q[1-4]|H[12])?$'");

            migrationBuilder.CreateIndex(
                name: "IX_EquityDocumentExtractions_DocumentId_ExtractorVersion",
                table: "EquityDocumentExtractions",
                columns: new[] { "DocumentId", "ExtractorVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityFinancialFacts_DocumentId",
                table: "EquityFinancialFacts",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_EquityFinancialFacts_ExtractionId",
                table: "EquityFinancialFacts",
                column: "ExtractionId");

            migrationBuilder.CreateIndex(
                name: "IX_EquityFinancialFacts_FactKey",
                table: "EquityFinancialFacts",
                column: "FactKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityFinancialFacts_Symbol_MetricCode_PeriodType_PeriodEnd",
                table: "EquityFinancialFacts",
                columns: new[] { "Symbol", "MetricCode", "PeriodType", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_EquityPipelineRuns_Symbol_StartedAtUtc",
                table: "EquityPipelineRuns",
                columns: new[] { "Symbol", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_EquityPipelineRuns_OneRunningPerSymbol",
                table: "EquityPipelineRuns",
                column: "Symbol",
                unique: true,
                filter: "\"Status\" = 'running'");

            migrationBuilder.CreateIndex(
                name: "IX_EquitySourceAttempts_RunId",
                table: "EquitySourceAttempts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_EquitySourceAttempts_Symbol_TargetPeriod_AttemptedAtUtc",
                table: "EquitySourceAttempts",
                columns: new[] { "Symbol", "TargetPeriod", "AttemptedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EquitySourceDocumentLocations_DocumentId_SourceUrl",
                table: "EquitySourceDocumentLocations",
                columns: new[] { "DocumentId", "SourceUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquitySourceDocumentLocations_SourceUrl",
                table: "EquitySourceDocumentLocations",
                column: "SourceUrl");

            // EQX v3: facts are insert-only except Status/ValidationJson (quarantine/validation state).
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION ""fn_equity_financial_facts_core_immutable""() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN IF (NEW.""Id"", NEW.""FactKey"", NEW.""Symbol"", NEW.""MetricCode"", NEW.""MetricDefinitionVersion"", NEW.""MetricGroup"", NEW.""Statement"", NEW.""PeriodLabel"", NEW.""PeriodType"", NEW.""PeriodStart"", NEW.""PeriodEnd"", NEW.""FiscalYear"", NEW.""Consolidation"", NEW.""Value"", NEW.""Unit"", NEW.""Scale"", NEW.""RawLabel"", NEW.""RawValueText"", NEW.""RawUnitText"", NEW.""ColumnRole"", NEW.""PageNumber"", NEW.""Location"", NEW.""DocumentId"", NEW.""ExtractionId"", NEW.""IsDerived"", NEW.""DerivationJson"", NEW.""AvailableAtUtc"", NEW.""CreatedAtUtc"") IS DISTINCT FROM (OLD.""Id"", OLD.""FactKey"", OLD.""Symbol"", OLD.""MetricCode"", OLD.""MetricDefinitionVersion"", OLD.""MetricGroup"", OLD.""Statement"", OLD.""PeriodLabel"", OLD.""PeriodType"", OLD.""PeriodStart"", OLD.""PeriodEnd"", OLD.""FiscalYear"", OLD.""Consolidation"", OLD.""Value"", OLD.""Unit"", OLD.""Scale"", OLD.""RawLabel"", OLD.""RawValueText"", OLD.""RawUnitText"", OLD.""ColumnRole"", OLD.""PageNumber"", OLD.""Location"", OLD.""DocumentId"", OLD.""ExtractionId"", OLD.""IsDerived"", OLD.""DerivationJson"", OLD.""AvailableAtUtc"", OLD.""CreatedAtUtc"") THEN RAISE EXCEPTION 'EquityFinancialFacts core columns are immutable; only Status/ValidationJson may change' USING ERRCODE = '23514'; END IF; RETURN NEW; END
$fn$;
DROP TRIGGER IF EXISTS ""TR_EquityFinancialFacts_CoreImmutable"" ON ""EquityFinancialFacts"";
CREATE TRIGGER ""TR_EquityFinancialFacts_CoreImmutable"" BEFORE UPDATE ON ""EquityFinancialFacts"" FOR EACH ROW EXECUTE FUNCTION ""fn_equity_financial_facts_core_immutable""();");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_EquityFinancialFacts_CoreImmutable"" ON ""EquityFinancialFacts""; DROP FUNCTION IF EXISTS ""fn_equity_financial_facts_core_immutable""();");

            migrationBuilder.DropTable(
                name: "EquityFinancialFacts");

            migrationBuilder.DropTable(
                name: "EquityPipelineRuns");

            migrationBuilder.DropTable(
                name: "EquitySourceAttempts");

            migrationBuilder.DropTable(
                name: "EquitySourceDocumentLocations");

            migrationBuilder.DropTable(
                name: "EquityDocumentExtractions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EquitySourceDocuments_Period",
                table: "EquitySourceDocuments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquitySourceDocuments_Period",
                table: "EquitySourceDocuments",
                sql: "\"ReportingPeriod\" IS NULL OR \"ReportingPeriod\" ~ '^[0-9]{4}(Q[1-4])?$'");
        }
    }
}
