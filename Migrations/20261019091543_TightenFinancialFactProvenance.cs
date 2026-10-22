using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class TightenFinancialFactProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityFinancialFacts_Provenance",
                table: "EquityFinancialFacts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityFinancialFacts_Provenance",
                table: "EquityFinancialFacts",
                sql: "(\"IsDerived\" AND \"DerivationJson\" IS NOT NULL AND \"ExtractionId\" IS NULL AND \"ColumnRole\" = 'derived') OR (NOT \"IsDerived\" AND \"DerivationJson\" IS NULL AND \"ExtractionId\" IS NOT NULL AND \"PageNumber\" IS NOT NULL AND \"PageNumber\" > 0 AND NULLIF(BTRIM(\"RawValueText\"), '') IS NOT NULL AND \"ColumnRole\" <> 'derived')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EquityFinancialFacts_Provenance",
                table: "EquityFinancialFacts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquityFinancialFacts_Provenance",
                table: "EquityFinancialFacts",
                sql: "(\"IsDerived\" AND \"DerivationJson\" IS NOT NULL AND \"ExtractionId\" IS NULL AND \"ColumnRole\" = 'derived') OR (NOT \"IsDerived\" AND \"DerivationJson\" IS NULL AND \"ExtractionId\" IS NOT NULL AND \"PageNumber\" > 0 AND NULLIF(BTRIM(\"RawValueText\"), '') IS NOT NULL AND \"ColumnRole\" <> 'derived')");
        }
    }
}
