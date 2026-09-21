using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddCausalSmartMoneyAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RegimeTransitions_Symbol_Timeframe_TransitionTimeMs",
                table: "RegimeTransitions");

            migrationBuilder.AddColumn<long>(
                name: "AvailableTimeMs",
                table: "SmartMoneyStructures",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "CalculationVersion",
                table: "SmartMoneyStructures",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "MitigatedAtMs",
                table: "SmartMoneyStructures",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OriginTimeMs",
                table: "SmartMoneyStructures",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ReferenceTimeMs",
                table: "SmartMoneyStructures",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "SmartMoneyStructures"
                SET "OriginTimeMs" = "TimeMs",
                    "AvailableTimeMs" = "TimeMs",
                    "CalculationVersion" = 'legacy-uncausal-v1';

                DELETE FROM "RegimeTransitions" duplicate
                USING "RegimeTransitions" canonical
                WHERE duplicate."Symbol" = canonical."Symbol"
                  AND duplicate."Timeframe" = canonical."Timeframe"
                  AND duplicate."TransitionTimeMs" = canonical."TransitionTimeMs"
                  AND duplicate."Id" > canonical."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SmartMoneyStructures_Symbol_Timeframe_AvailableTimeMs",
                table: "SmartMoneyStructures",
                columns: new[] { "Symbol", "Timeframe", "AvailableTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_SmartMoneyStructures_Symbol_Timeframe_EventType_OriginTimeM~",
                table: "SmartMoneyStructures",
                columns: new[] { "Symbol", "Timeframe", "EventType", "OriginTimeMs", "AvailableTimeMs", "CalculationVersion" },
                unique: true,
                filter: "\"CalculationVersion\" = 'smc-causal-v2'");

            migrationBuilder.CreateIndex(
                name: "IX_RegimeTransitions_Symbol_Timeframe_TransitionTimeMs",
                table: "RegimeTransitions",
                columns: new[] { "Symbol", "Timeframe", "TransitionTimeMs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SmartMoneyStructures_Symbol_Timeframe_AvailableTimeMs",
                table: "SmartMoneyStructures");

            migrationBuilder.DropIndex(
                name: "IX_SmartMoneyStructures_Symbol_Timeframe_EventType_OriginTimeM~",
                table: "SmartMoneyStructures");

            migrationBuilder.DropIndex(
                name: "IX_RegimeTransitions_Symbol_Timeframe_TransitionTimeMs",
                table: "RegimeTransitions");

            migrationBuilder.DropColumn(
                name: "AvailableTimeMs",
                table: "SmartMoneyStructures");

            migrationBuilder.DropColumn(
                name: "CalculationVersion",
                table: "SmartMoneyStructures");

            migrationBuilder.DropColumn(
                name: "MitigatedAtMs",
                table: "SmartMoneyStructures");

            migrationBuilder.DropColumn(
                name: "OriginTimeMs",
                table: "SmartMoneyStructures");

            migrationBuilder.DropColumn(
                name: "ReferenceTimeMs",
                table: "SmartMoneyStructures");

            migrationBuilder.CreateIndex(
                name: "IX_RegimeTransitions_Symbol_Timeframe_TransitionTimeMs",
                table: "RegimeTransitions",
                columns: new[] { "Symbol", "Timeframe", "TransitionTimeMs" });
        }
    }
}
