using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddEnsembleReevaluationLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourcePredictionId",
                table: "EnsemblePredictionRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnsemblePredictionRecords_SourcePredictionId_EvaluationVers~",
                table: "EnsemblePredictionRecords",
                columns: new[] { "SourcePredictionId", "EvaluationVersion" },
                unique: true,
                filter: "\"SourcePredictionId\" IS NOT NULL");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EnsemblePredictionRecords_SourcePredictionId_EvaluationVers~",
                table: "EnsemblePredictionRecords");

            migrationBuilder.DropColumn(
                name: "SourcePredictionId",
                table: "EnsemblePredictionRecords");
        }
    }
}
