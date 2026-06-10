using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddKlinesTechnicalIndicatorsMarketMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Klines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    CloseTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    Open = table.Column<decimal>(type: "numeric", nullable: false),
                    High = table.Column<decimal>(type: "numeric", nullable: false),
                    Low = table.Column<decimal>(type: "numeric", nullable: false),
                    Close = table.Column<decimal>(type: "numeric", nullable: false),
                    Volume = table.Column<decimal>(type: "numeric", nullable: false),
                    QuoteVolume = table.Column<decimal>(type: "numeric", nullable: false),
                    TradeCount = table.Column<int>(type: "integer", nullable: false),
                    TakerBuyVolume = table.Column<decimal>(type: "numeric", nullable: false),
                    TakerBuyQuoteVolume = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Klines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    FundingRate = table.Column<double>(type: "double precision", nullable: true),
                    OpenInterest = table.Column<double>(type: "double precision", nullable: true),
                    LongShortRatio = table.Column<double>(type: "double precision", nullable: true),
                    LongLiquidationUsd = table.Column<double>(type: "double precision", nullable: true),
                    ShortLiquidationUsd = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TechnicalIndicators",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    Rsi14 = table.Column<double>(type: "double precision", nullable: true),
                    Ema12 = table.Column<decimal>(type: "numeric", nullable: true),
                    Ema26 = table.Column<decimal>(type: "numeric", nullable: true),
                    Ema50 = table.Column<decimal>(type: "numeric", nullable: true),
                    Ema200 = table.Column<decimal>(type: "numeric", nullable: true),
                    Macd = table.Column<double>(type: "double precision", nullable: true),
                    MacdSignal = table.Column<double>(type: "double precision", nullable: true),
                    MacdHistogram = table.Column<double>(type: "double precision", nullable: true),
                    BollingerUpper = table.Column<decimal>(type: "numeric", nullable: true),
                    BollingerMiddle = table.Column<decimal>(type: "numeric", nullable: true),
                    BollingerLower = table.Column<decimal>(type: "numeric", nullable: true),
                    Atr14 = table.Column<double>(type: "double precision", nullable: true),
                    Obv = table.Column<double>(type: "double precision", nullable: true),
                    Vwap = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicalIndicators", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Klines_Symbol_Timeframe_CloseTimeMs",
                table: "Klines",
                columns: new[] { "Symbol", "Timeframe", "CloseTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_Klines_Symbol_Timeframe_OpenTimeMs",
                table: "Klines",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketMetrics_Symbol_Timeframe_OpenTimeMs",
                table: "MarketMetrics",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalIndicators_Symbol_Timeframe_OpenTimeMs",
                table: "TechnicalIndicators",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Klines");

            migrationBuilder.DropTable(
                name: "MarketMetrics");

            migrationBuilder.DropTable(
                name: "TechnicalIndicators");
        }
    }
}
