using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketRegimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketRegimes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Timeframe = table.Column<string>(type: "text", nullable: false),
                    OpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    RegimeType = table.Column<string>(type: "text", nullable: false),
                    TrendStrength = table.Column<double>(type: "double precision", nullable: false),
                    VolatilityScore = table.Column<double>(type: "double precision", nullable: false),
                    Adx = table.Column<double>(type: "double precision", nullable: false),
                    PlusDi = table.Column<double>(type: "double precision", nullable: false),
                    MinusDi = table.Column<double>(type: "double precision", nullable: false),
                    AtrRatio = table.Column<double>(type: "double precision", nullable: false),
                    BollingerBandwidth = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketRegimes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegimeTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Timeframe = table.Column<string>(type: "text", nullable: false),
                    FromRegime = table.Column<string>(type: "text", nullable: false),
                    ToRegime = table.Column<string>(type: "text", nullable: false),
                    TransitionTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    DurationBars = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegimeTransitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketRegimes_Symbol_Timeframe_OpenTimeMs",
                table: "MarketRegimes",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketRegimes_Symbol_Timeframe_RegimeType",
                table: "MarketRegimes",
                columns: new[] { "Symbol", "Timeframe", "RegimeType" });

            migrationBuilder.CreateIndex(
                name: "IX_RegimeTransitions_Symbol_Timeframe_TransitionTimeMs",
                table: "RegimeTransitions",
                columns: new[] { "Symbol", "Timeframe", "TransitionTimeMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketRegimes");

            migrationBuilder.DropTable(
                name: "RegimeTransitions");
        }
    }
}
