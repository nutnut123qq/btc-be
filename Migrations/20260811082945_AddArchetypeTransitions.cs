using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddArchetypeTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchetypeSequences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FirstArchetypeId = table.Column<long>(type: "bigint", nullable: false),
                    SecondArchetypeId = table.Column<long>(type: "bigint", nullable: false),
                    ThirdArchetypeId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    OutcomeUpRate = table.Column<double>(type: "double precision", nullable: false),
                    OutcomeDownRate = table.Column<double>(type: "double precision", nullable: false),
                    OutcomeSidewaysRate = table.Column<double>(type: "double precision", nullable: false),
                    AvgReturnPct = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchetypeSequences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchetypeSequences_CandleArchetypes_FirstArchetypeId",
                        column: x => x.FirstArchetypeId,
                        principalTable: "CandleArchetypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArchetypeSequences_CandleArchetypes_SecondArchetypeId",
                        column: x => x.SecondArchetypeId,
                        principalTable: "CandleArchetypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArchetypeSequences_CandleArchetypes_ThirdArchetypeId",
                        column: x => x.ThirdArchetypeId,
                        principalTable: "CandleArchetypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ArchetypeTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FromArchetypeId = table.Column<long>(type: "bigint", nullable: false),
                    ToArchetypeId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    TransitionCount = table.Column<int>(type: "integer", nullable: false),
                    TransitionProbability = table.Column<double>(type: "double precision", nullable: false),
                    AvgReturnPct = table.Column<double>(type: "double precision", nullable: false),
                    AvgBarsToTransition = table.Column<double>(type: "double precision", nullable: false),
                    LastSeenMs = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchetypeTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchetypeTransitions_CandleArchetypes_FromArchetypeId",
                        column: x => x.FromArchetypeId,
                        principalTable: "CandleArchetypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArchetypeTransitions_CandleArchetypes_ToArchetypeId",
                        column: x => x.ToArchetypeId,
                        principalTable: "CandleArchetypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchetypeSequences_FirstArchetypeId_SecondArchetypeId_Third~",
                table: "ArchetypeSequences",
                columns: new[] { "FirstArchetypeId", "SecondArchetypeId", "ThirdArchetypeId", "Symbol", "Timeframe", "WindowSize" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchetypeSequences_SecondArchetypeId",
                table: "ArchetypeSequences",
                column: "SecondArchetypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchetypeSequences_Symbol_Timeframe_WindowSize",
                table: "ArchetypeSequences",
                columns: new[] { "Symbol", "Timeframe", "WindowSize" });

            migrationBuilder.CreateIndex(
                name: "IX_ArchetypeSequences_ThirdArchetypeId",
                table: "ArchetypeSequences",
                column: "ThirdArchetypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchetypeTransitions_FromArchetypeId_ToArchetypeId_Symbol_T~",
                table: "ArchetypeTransitions",
                columns: new[] { "FromArchetypeId", "ToArchetypeId", "Symbol", "Timeframe", "WindowSize" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchetypeTransitions_Symbol_Timeframe_WindowSize",
                table: "ArchetypeTransitions",
                columns: new[] { "Symbol", "Timeframe", "WindowSize" });

            migrationBuilder.CreateIndex(
                name: "IX_ArchetypeTransitions_ToArchetypeId",
                table: "ArchetypeTransitions",
                column: "ToArchetypeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchetypeSequences");

            migrationBuilder.DropTable(
                name: "ArchetypeTransitions");
        }
    }
}
