using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class TightenMbbObservationFillCoherence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MbbSignalObservations_FillCoherence",
                table: "MbbSignalObservations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MbbSignalObservations_FillCoherence",
                table: "MbbSignalObservations",
                sql: "(\"FillPrice\" IS NULL AND \"FillObservedAtUtc\" IS NULL AND \"FillSource\" IS NULL AND \"FillBarOpenTimeMs\" IS NULL) OR (\"Decision\" IN ('buy-more','reduce','sell','conditional-sell') AND \"FillPrice\" > 0 AND \"FillObservedAtUtc\" >= \"ObservedAtUtc\" AND NULLIF(BTRIM(\"FillSource\"), '') IS NOT NULL AND \"FillBarOpenTimeMs\" >= \"AvailableTimeMs\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MbbSignalObservations_FillCoherence",
                table: "MbbSignalObservations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MbbSignalObservations_FillCoherence",
                table: "MbbSignalObservations",
                sql: "(\"FillPrice\" IS NULL AND \"FillObservedAtUtc\" IS NULL AND \"FillSource\" IS NULL AND \"FillBarOpenTimeMs\" IS NULL) OR (\"Decision\" IN ('buy-more','reduce','sell','conditional-sell') AND \"FillPrice\" > 0 AND \"FillObservedAtUtc\" >= \"ObservedAtUtc\" AND NULLIF(BTRIM(\"FillSource\"), '') IS NOT NULL AND \"FillBarOpenTimeMs\" > \"SignalBarOpenTimeMs\")");
        }
    }
}
