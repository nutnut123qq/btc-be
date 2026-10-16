using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMbbSignalObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MbbSignalObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RecorderVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SignalBarOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    SignalBarCloseTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    Decision = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RulesVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    EvidenceStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PriceBasis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MarketCloseVnd = table.Column<decimal>(type: "numeric", nullable: true),
                    DecisionPayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    FillPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    FillObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FillSource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FillBarOpenTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    OutcomeReturn = table.Column<double>(type: "double precision", nullable: true),
                    OutcomeObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OutcomeHorizon = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MbbSignalObservations", x => x.Id);
                    table.CheckConstraint("CK_MbbSignalObservations_Chronology", "\"AvailableTimeMs\" >= \"SignalBarCloseTimeMs\"");
                    table.CheckConstraint("CK_MbbSignalObservations_Decision", "\"Decision\" IN ('hold','wait','buy-more','reduce','sell','conditional-sell')");
                    table.CheckConstraint("CK_MbbSignalObservations_FillCoherence", "(\"FillPrice\" IS NULL AND \"FillObservedAtUtc\" IS NULL AND \"FillSource\" IS NULL AND \"FillBarOpenTimeMs\" IS NULL) OR (\"Decision\" IN ('buy-more','reduce','sell','conditional-sell') AND \"FillPrice\" > 0 AND \"FillObservedAtUtc\" >= \"ObservedAtUtc\" AND NULLIF(BTRIM(\"FillSource\"), '') IS NOT NULL AND \"FillBarOpenTimeMs\" > \"SignalBarOpenTimeMs\")");
                    table.CheckConstraint("CK_MbbSignalObservations_OutcomeCoherence", "(\"OutcomeReturn\" IS NULL AND \"OutcomeObservedAtUtc\" IS NULL AND \"OutcomeHorizon\" IS NULL) OR (\"FillPrice\" IS NOT NULL AND \"OutcomeReturn\" IS NOT NULL AND \"OutcomeObservedAtUtc\" >= \"FillObservedAtUtc\" AND NULLIF(BTRIM(\"OutcomeHorizon\"), '') IS NOT NULL)");
                    table.CheckConstraint("CK_MbbSignalObservations_ProspectiveClock", "ABS(\"AvailableTimeMs\" - (EXTRACT(EPOCH FROM \"ObservedAtUtc\") * 1000)::bigint) <= 1000");
                    table.CheckConstraint("CK_MbbSignalObservations_Scope", "\"Symbol\" = 'MBB' AND \"Timeframe\" IN ('1h','1d')");
                    table.CheckConstraint("CK_MbbSignalObservations_SignalBar", "\"SignalBarOpenTimeMs\" < \"SignalBarCloseTimeMs\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MbbSignalObservations_DecisionId",
                table: "MbbSignalObservations",
                column: "DecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MbbSignalObservations_Symbol_Timeframe_SignalBarOpenTimeMs",
                table: "MbbSignalObservations",
                columns: new[] { "Symbol", "Timeframe", "SignalBarOpenTimeMs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MbbSignalObservations");
        }
    }
}
