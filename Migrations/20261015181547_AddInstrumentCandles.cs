using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddInstrumentCandles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstrumentCandles",
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
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PriceBasis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Venue = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceTimezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentCandles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentCandles_Symbol_Timeframe_CloseTimeMs",
                table: "InstrumentCandles",
                columns: new[] { "Symbol", "Timeframe", "CloseTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentCandles_Symbol_Timeframe_OpenTimeMs",
                table: "InstrumentCandles",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstrumentCandles");
        }
    }
}
