using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMlFeatureStoreAndTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MacdHistogramNorm",
                table: "TechnicalIndicators",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MacdNorm",
                table: "TechnicalIndicators",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MacdSignalNorm",
                table: "TechnicalIndicators",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ObvEma50",
                table: "TechnicalIndicators",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RollingVwap24",
                table: "TechnicalIndicators",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FundingRateZscore",
                table: "MarketMetrics",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OiDeltaPct",
                table: "MarketMetrics",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MlFeatureStores",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    CloseZscore = table.Column<double>(type: "double precision", nullable: true),
                    ClosePctChange1 = table.Column<double>(type: "double precision", nullable: true),
                    ClosePctChange4 = table.Column<double>(type: "double precision", nullable: true),
                    ClosePctChange24 = table.Column<double>(type: "double precision", nullable: true),
                    HighLowRangePct = table.Column<double>(type: "double precision", nullable: true),
                    BodyPct = table.Column<double>(type: "double precision", nullable: true),
                    UpperWickPct = table.Column<double>(type: "double precision", nullable: true),
                    LowerWickPct = table.Column<double>(type: "double precision", nullable: true),
                    Rsi14 = table.Column<double>(type: "double precision", nullable: true),
                    Rsi14Slope = table.Column<double>(type: "double precision", nullable: true),
                    MacdNorm = table.Column<double>(type: "double precision", nullable: true),
                    MacdSignalNorm = table.Column<double>(type: "double precision", nullable: true),
                    MacdHistogramNorm = table.Column<double>(type: "double precision", nullable: true),
                    Ema12Dist = table.Column<double>(type: "double precision", nullable: true),
                    Ema26Dist = table.Column<double>(type: "double precision", nullable: true),
                    Ema50Dist = table.Column<double>(type: "double precision", nullable: true),
                    Ema200Dist = table.Column<double>(type: "double precision", nullable: true),
                    BollingerWidth = table.Column<double>(type: "double precision", nullable: true),
                    BollingerPosition = table.Column<double>(type: "double precision", nullable: true),
                    Atr14Pct = table.Column<double>(type: "double precision", nullable: true),
                    ObvEmaDist = table.Column<double>(type: "double precision", nullable: true),
                    VwapDist = table.Column<double>(type: "double precision", nullable: true),
                    RollingVwapDist = table.Column<double>(type: "double precision", nullable: true),
                    VolumeZscore = table.Column<double>(type: "double precision", nullable: true),
                    VolumeSma20Ratio = table.Column<double>(type: "double precision", nullable: true),
                    TakerBuyRatio = table.Column<double>(type: "double precision", nullable: true),
                    FundingRateZscore = table.Column<double>(type: "double precision", nullable: true),
                    OiDeltaPct = table.Column<double>(type: "double precision", nullable: true),
                    LongLiquidationUsd = table.Column<double>(type: "double precision", nullable: true),
                    ShortLiquidationUsd = table.Column<double>(type: "double precision", nullable: true),
                    RecentPatternEncoded = table.Column<int>(type: "integer", nullable: true),
                    ActiveRuleCount = table.Column<int>(type: "integer", nullable: true),
                    PcaComponent1 = table.Column<double>(type: "double precision", nullable: true),
                    PcaComponent2 = table.Column<double>(type: "double precision", nullable: true),
                    PcaComponent3 = table.Column<double>(type: "double precision", nullable: true),
                    PcaComponent4 = table.Column<double>(type: "double precision", nullable: true),
                    PcaComponent5 = table.Column<double>(type: "double precision", nullable: true),
                    NullRatio = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MlFeatureStores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PatternSequences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EndTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    PatternChainJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatternSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceTargets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    TargetReturn1h = table.Column<double>(type: "double precision", nullable: true),
                    TargetDirection1h = table.Column<int>(type: "integer", nullable: true),
                    TargetReturn4h = table.Column<double>(type: "double precision", nullable: true),
                    TargetDirection4h = table.Column<int>(type: "integer", nullable: true),
                    TargetReturn1d = table.Column<double>(type: "double precision", nullable: true),
                    TargetDirection1d = table.Column<int>(type: "integer", nullable: true),
                    TargetReturn3d = table.Column<double>(type: "double precision", nullable: true),
                    TargetDirection3d = table.Column<int>(type: "integer", nullable: true),
                    TargetReturn7d = table.Column<double>(type: "double precision", nullable: true),
                    TargetDirection7d = table.Column<int>(type: "integer", nullable: true),
                    TargetVolatility1d = table.Column<double>(type: "double precision", nullable: true),
                    TargetMaxDrawdown1d = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceTargets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MlFeatureStores_Symbol_Timeframe_CreatedAtUtc",
                table: "MlFeatureStores",
                columns: new[] { "Symbol", "Timeframe", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MlFeatureStores_Symbol_Timeframe_OpenTimeMs",
                table: "MlFeatureStores",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatternSequences_Symbol_Timeframe_StartTimeMs",
                table: "PatternSequences",
                columns: new[] { "Symbol", "Timeframe", "StartTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_PatternSequences_Symbol_Timeframe_WindowSize",
                table: "PatternSequences",
                columns: new[] { "Symbol", "Timeframe", "WindowSize" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceTargets_Symbol_Timeframe_OpenTimeMs",
                table: "PriceTargets",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MlFeatureStores");

            migrationBuilder.DropTable(
                name: "PatternSequences");

            migrationBuilder.DropTable(
                name: "PriceTargets");

            migrationBuilder.DropColumn(
                name: "MacdHistogramNorm",
                table: "TechnicalIndicators");

            migrationBuilder.DropColumn(
                name: "MacdNorm",
                table: "TechnicalIndicators");

            migrationBuilder.DropColumn(
                name: "MacdSignalNorm",
                table: "TechnicalIndicators");

            migrationBuilder.DropColumn(
                name: "ObvEma50",
                table: "TechnicalIndicators");

            migrationBuilder.DropColumn(
                name: "RollingVwap24",
                table: "TechnicalIndicators");

            migrationBuilder.DropColumn(
                name: "FundingRateZscore",
                table: "MarketMetrics");

            migrationBuilder.DropColumn(
                name: "OiDeltaPct",
                table: "MarketMetrics");
        }
    }
}
