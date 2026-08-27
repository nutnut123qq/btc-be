using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddLiquidationSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiquidationSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentPrice = table.Column<double>(type: "double precision", nullable: false),
                    TotalLongLiqUsdt = table.Column<double>(type: "double precision", nullable: false),
                    TotalShortLiqUsdt = table.Column<double>(type: "double precision", nullable: false),
                    HeatmapJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiquidationSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiquidationSnapshots_Symbol_Timeframe_TimestampUtc",
                table: "LiquidationSnapshots",
                columns: new[] { "Symbol", "Timeframe", "TimestampUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiquidationSnapshots");
        }
    }
}
