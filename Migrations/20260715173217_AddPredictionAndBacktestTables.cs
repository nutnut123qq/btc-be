using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPredictionAndBacktestTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacktestRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    Horizon = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ModelName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StartTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EndTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    FeeBps = table.Column<double>(type: "double precision", nullable: false),
                    SlippageBps = table.Column<double>(type: "double precision", nullable: false),
                    TotalTrades = table.Column<int>(type: "integer", nullable: false),
                    WinRate = table.Column<double>(type: "double precision", nullable: false),
                    TotalReturnPct = table.Column<double>(type: "double precision", nullable: false),
                    BuyHoldReturnPct = table.Column<double>(type: "double precision", nullable: false),
                    MaxDrawdownPct = table.Column<double>(type: "double precision", nullable: false),
                    SharpeRatio = table.Column<double>(type: "double precision", nullable: false),
                    SortinoRatio = table.Column<double>(type: "double precision", nullable: false),
                    ProfitFactor = table.Column<double>(type: "double precision", nullable: false),
                    FinalEquity = table.Column<double>(type: "double precision", nullable: false),
                    MetricsJson = table.Column<string>(type: "text", nullable: false),
                    EquityCurveJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelPredictions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    Horizon = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PredictedLabel = table.Column<int>(type: "integer", nullable: false),
                    ProbDown = table.Column<double>(type: "double precision", nullable: false),
                    ProbSideways = table.Column<double>(type: "double precision", nullable: false),
                    ProbUp = table.Column<double>(type: "double precision", nullable: false),
                    TargetReturn = table.Column<double>(type: "double precision", nullable: true),
                    ModelVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WindowEndMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPredictions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BacktestTrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BacktestRunId = table.Column<int>(type: "integer", nullable: false),
                    EntryTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    ExitTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    Side = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    GrossReturn = table.Column<double>(type: "double precision", nullable: false),
                    NetReturn = table.Column<double>(type: "double precision", nullable: false),
                    PnlPct = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    TrueLabel = table.Column<int>(type: "integer", nullable: false),
                    TargetReturn = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestTrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BacktestTrades_BacktestRuns_BacktestRunId",
                        column: x => x.BacktestRunId,
                        principalTable: "BacktestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_Symbol_Timeframe_CreatedAtUtc",
                table: "BacktestRuns",
                columns: new[] { "Symbol", "Timeframe", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestTrades_BacktestRunId_EntryTimeMs",
                table: "BacktestTrades",
                columns: new[] { "BacktestRunId", "EntryTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelPredictions_Symbol_Timeframe_CreatedAtUtc",
                table: "ModelPredictions",
                columns: new[] { "Symbol", "Timeframe", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelPredictions_Symbol_Timeframe_WindowSize_Horizon_Window~",
                table: "ModelPredictions",
                columns: new[] { "Symbol", "Timeframe", "WindowSize", "Horizon", "WindowEndMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestTrades");

            migrationBuilder.DropTable(
                name: "ModelPredictions");

            migrationBuilder.DropTable(
                name: "BacktestRuns");
        }
    }
}
