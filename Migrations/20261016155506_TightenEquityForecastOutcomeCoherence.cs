using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class TightenEquityForecastOutcomeCoherence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityForecastRecords_OutcomeCoherence",
                table: "EquityForecastRecords");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityForecastRecords_OutcomeCoherence",
                table: "EquityForecastRecords",
                sql: "(\"EntryBarOpenTimeMs\" IS NULL AND \"EntryPrice\" IS NULL AND \"OutcomeLabel\" IS NULL AND \"OutcomeReturnNet\" IS NULL AND \"ResolvedBarOpenTimeMs\" IS NULL AND \"OutcomeResolvedAtUtc\" IS NULL) OR (\"OutcomeLabel\" IN ('target-first','stop-first','timeout') AND \"EntryPrice\" > 0 AND \"OutcomeReturnNet\" IS NOT NULL AND \"EntryBarOpenTimeMs\" IS NOT NULL AND \"ResolvedBarOpenTimeMs\" IS NOT NULL AND \"EntryBarOpenTimeMs\" > \"SignalBarOpenTimeMs\" AND \"EntryBarOpenTimeMs\" >= \"AvailableTimeMs\" AND \"ResolvedBarOpenTimeMs\" >= \"EntryBarOpenTimeMs\" AND \"OutcomeResolvedAtUtc\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityForecastRecords_OutcomeCoherence",
                table: "EquityForecastRecords");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityForecastRecords_OutcomeCoherence",
                table: "EquityForecastRecords",
                sql: "(\"EntryBarOpenTimeMs\" IS NULL AND \"EntryPrice\" IS NULL AND \"OutcomeLabel\" IS NULL AND \"OutcomeReturnNet\" IS NULL AND \"ResolvedBarOpenTimeMs\" IS NULL AND \"OutcomeResolvedAtUtc\" IS NULL) OR (\"OutcomeLabel\" IN ('target-first','stop-first','timeout') AND \"EntryPrice\" > 0 AND \"OutcomeReturnNet\" IS NOT NULL AND \"EntryBarOpenTimeMs\" > \"SignalBarOpenTimeMs\" AND \"EntryBarOpenTimeMs\" >= \"AvailableTimeMs\" AND \"ResolvedBarOpenTimeMs\" >= \"EntryBarOpenTimeMs\" AND \"OutcomeResolvedAtUtc\" IS NOT NULL)");
        }
    }
}
