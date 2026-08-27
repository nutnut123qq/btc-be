using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSentimentAndEnsemblePrediction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnsemblePredictionRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Timeframe = table.Column<string>(type: "text", nullable: false),
                    TimeMs = table.Column<long>(type: "bigint", nullable: false),
                    FinalDirection = table.Column<string>(type: "text", nullable: false),
                    ProbUp = table.Column<double>(type: "double precision", nullable: false),
                    ProbDown = table.Column<double>(type: "double precision", nullable: false),
                    ProbSideways = table.Column<double>(type: "double precision", nullable: false),
                    EnsembleConfidence = table.Column<double>(type: "double precision", nullable: false),
                    LayerBreakdownJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnsemblePredictionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentimentSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    TimeMs = table.Column<long>(type: "bigint", nullable: false),
                    FearGreedScore = table.Column<int>(type: "integer", nullable: false),
                    FundingRateZScore = table.Column<double>(type: "double precision", nullable: false),
                    LongShortRatio = table.Column<double>(type: "double precision", nullable: false),
                    NewsSentimentScore = table.Column<double>(type: "double precision", nullable: false),
                    AggregatedSentiment = table.Column<double>(type: "double precision", nullable: false),
                    SentimentLabel = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentimentSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnsemblePredictionRecords_Symbol_Timeframe_TimeMs",
                table: "EnsemblePredictionRecords",
                columns: new[] { "Symbol", "Timeframe", "TimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_SentimentSnapshots_Symbol_TimeMs",
                table: "SentimentSnapshots",
                columns: new[] { "Symbol", "TimeMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnsemblePredictionRecords");

            migrationBuilder.DropTable(
                name: "SentimentSnapshots");
        }
    }
}
