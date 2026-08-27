using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPredictionEvaluationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ActualPrice24h",
                table: "EnsemblePredictionRecords",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ActualReturnPct",
                table: "EnsemblePredictionRecords",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EntryPrice",
                table: "EnsemblePredictionRecords",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<long>(
                name: "EvaluatedAtMs",
                table: "EnsemblePredictionRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationStatus",
                table: "EnsemblePredictionRecords",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualPrice24h",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "ActualReturnPct",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "EntryPrice",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "EvaluatedAtMs",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "EvaluationStatus",
                table: "EnsemblePredictionRecords");
        }
    }
}
