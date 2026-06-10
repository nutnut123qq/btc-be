using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowVectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WindowVectors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FeatureType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    StartTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EndTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    Vector = table.Column<float[]>(type: "real[]", nullable: false),
                    VectorDim = table.Column<int>(type: "integer", nullable: false),
                    VectorNorm = table.Column<float>(type: "real", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowVectors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WindowVectors_Symbol_Timeframe_FeatureType_WindowSize_EndTi~",
                table: "WindowVectors",
                columns: new[] { "Symbol", "Timeframe", "FeatureType", "WindowSize", "EndTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_WindowVectors_Symbol_Timeframe_FeatureType_WindowSize_Start~",
                table: "WindowVectors",
                columns: new[] { "Symbol", "Timeframe", "FeatureType", "WindowSize", "StartTimeMs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WindowVectors");
        }
    }
}
