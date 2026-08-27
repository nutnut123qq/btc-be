using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTimeframeConfluence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfluenceSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    TimeMs = table.Column<long>(type: "bigint", nullable: false),
                    ConfluenceScore = table.Column<double>(type: "double precision", nullable: false),
                    OverallDirection = table.Column<string>(type: "text", nullable: false),
                    TimeframeAlignmentsJson = table.Column<string>(type: "text", nullable: false),
                    HasConflict = table.Column<bool>(type: "boolean", nullable: false),
                    ConflictDetails = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfluenceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfluenceSnapshots_Symbol_TimeMs",
                table: "ConfluenceSnapshots",
                columns: new[] { "Symbol", "TimeMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfluenceSnapshots");
        }
    }
}
