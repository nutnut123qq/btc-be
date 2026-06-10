using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddCandleVolumeStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandleVolumeStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    Volume = table.Column<decimal>(type: "numeric", nullable: false),
                    VolumeSma20 = table.Column<decimal>(type: "numeric", nullable: false),
                    VolumeAnomalyRatio = table.Column<double>(type: "double precision", nullable: false),
                    VolumeVsPrevious = table.Column<double>(type: "double precision", nullable: false),
                    VolumeVsMax10 = table.Column<double>(type: "double precision", nullable: false),
                    VolumeTrend = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandleVolumeStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandleVolumeStats_Symbol_Timeframe_OpenTimeMs",
                table: "CandleVolumeStats",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeMs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandleVolumeStats_Symbol_Timeframe_VolumeAnomalyRatio",
                table: "CandleVolumeStats",
                columns: new[] { "Symbol", "Timeframe", "VolumeAnomalyRatio" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandleVolumeStats");
        }
    }
}
