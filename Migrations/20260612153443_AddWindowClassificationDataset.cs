using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowClassificationDataset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WindowClassificationDatasets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    Horizon = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowStartMs = table.Column<long>(type: "bigint", nullable: false),
                    WindowEndMs = table.Column<long>(type: "bigint", nullable: false),
                    FeatureVector = table.Column<float[]>(type: "real[]", nullable: false),
                    FeatureDim = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<int>(type: "integer", nullable: false),
                    TargetReturn = table.Column<double>(type: "double precision", nullable: true),
                    WindowNullRatio = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowClassificationDatasets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WindowClassificationDatasets_Symbol_Timeframe_WindowSize_H~1",
                table: "WindowClassificationDatasets",
                columns: new[] { "Symbol", "Timeframe", "WindowSize", "Horizon", "WindowStartMs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WindowClassificationDatasets_Symbol_Timeframe_WindowSize_Ho~",
                table: "WindowClassificationDatasets",
                columns: new[] { "Symbol", "Timeframe", "WindowSize", "Horizon", "Label" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WindowClassificationDatasets");
        }
    }
}
