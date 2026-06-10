using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddCandlePatterns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandlePatterns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    Open = table.Column<decimal>(type: "numeric", nullable: false),
                    High = table.Column<decimal>(type: "numeric", nullable: false),
                    Low = table.Column<decimal>(type: "numeric", nullable: false),
                    Close = table.Column<decimal>(type: "numeric", nullable: false),
                    Volume = table.Column<decimal>(type: "numeric", nullable: false),
                    PatternType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PatternCategory = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TrendDirection = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandlePatterns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandlePatterns_Symbol_Timeframe_OpenTimeMs",
                table: "CandlePatterns",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_CandlePatterns_Symbol_Timeframe_OpenTimeMs_PatternType",
                table: "CandlePatterns",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs", "PatternType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandlePatterns_Symbol_Timeframe_PatternType",
                table: "CandlePatterns",
                columns: new[] { "Symbol", "Timeframe", "PatternType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandlePatterns");
        }
    }
}
