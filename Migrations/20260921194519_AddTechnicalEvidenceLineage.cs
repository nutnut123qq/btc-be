using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTechnicalEvidenceLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TechnicalEvidenceRebuildCheckpoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ModuleContractVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ModuleContractSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastProcessedCloseTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    CoverageStartCloseTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    HistoricalBackfill = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MaterializedRecordCount = table.Column<long>(type: "bigint", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicalEvidenceRebuildCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TechnicalEvidenceRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LayerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AsOfTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    ModuleContractVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ModuleContractSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CalculationVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Availability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AvailableTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    SourceStartTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    SourceEndTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    SourceCandleCount = table.Column<int>(type: "integer", nullable: false),
                    EnvelopeJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicalEvidenceRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalEvidenceRebuildCheckpoints_Symbol_Timeframe_Module~",
                table: "TechnicalEvidenceRebuildCheckpoints",
                columns: new[] { "Symbol", "Timeframe", "ModuleContractVersion", "ModuleContractSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalEvidenceRecords_Symbol_Timeframe_AsOfTimeMs",
                table: "TechnicalEvidenceRecords",
                columns: new[] { "Symbol", "Timeframe", "AsOfTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalEvidenceRecords_Symbol_Timeframe_LayerKey_AsOfTime~",
                table: "TechnicalEvidenceRecords",
                columns: new[] { "Symbol", "Timeframe", "LayerKey", "AsOfTimeMs", "ModuleContractVersion", "ModuleContractSha256", "CalculationVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TechnicalEvidenceRebuildCheckpoints");

            migrationBuilder.DropTable(
                name: "TechnicalEvidenceRecords");
        }
    }
}
