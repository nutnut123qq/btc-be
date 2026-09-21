using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleDiscoveryConfidenceIntervals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "EvaluationWinRateCi95High",
                table: "RuleDiscoveryTrials",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EvaluationWinRateCi95Low",
                table: "RuleDiscoveryTrials",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OosWinRateCi95High",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OosWinRateCi95Low",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvaluationWinRateCi95High",
                table: "RuleDiscoveryTrials");

            migrationBuilder.DropColumn(
                name: "EvaluationWinRateCi95Low",
                table: "RuleDiscoveryTrials");

            migrationBuilder.DropColumn(
                name: "OosWinRateCi95High",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "OosWinRateCi95Low",
                table: "CandleSequenceRules");
        }
    }
}
