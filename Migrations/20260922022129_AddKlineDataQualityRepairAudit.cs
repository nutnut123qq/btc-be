using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddKlineDataQualityRepairAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KlineDataRepairRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlanSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceEvidenceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IssueType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EndOpenTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    RequestedBars = table.Column<int>(type: "integer", nullable: false),
                    VerifiedSourceBars = table.Column<int>(type: "integer", nullable: false),
                    InsertedBars = table.Column<int>(type: "integer", nullable: false),
                    ReplacedBars = table.Column<int>(type: "integer", nullable: false),
                    NoopBars = table.Column<int>(type: "integer", nullable: false),
                    UnresolvedBars = table.Column<int>(type: "integer", nullable: false),
                    UnresolvedOpenTimeMsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourceClassification = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceEvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    BeforeEvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourceCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KlineDataRepairRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KlineDataRepairRuns_PlanSha256",
                table: "KlineDataRepairRuns",
                column: "PlanSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KlineDataRepairRuns_Symbol_Timeframe_AppliedAtUtc",
                table: "KlineDataRepairRuns",
                columns: new[] { "Symbol", "Timeframe", "AppliedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KlineDataRepairRuns");
        }
    }
}
