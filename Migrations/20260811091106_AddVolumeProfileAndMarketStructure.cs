using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddVolumeProfileAndMarketStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SmartMoneyStructures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Timeframe = table.Column<string>(type: "text", nullable: false),
                    TimeMs = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Price = table.Column<double>(type: "double precision", nullable: false),
                    HighPrice = table.Column<double>(type: "double precision", nullable: true),
                    LowPrice = table.Column<double>(type: "double precision", nullable: true),
                    IsMitigated = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmartMoneyStructures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VolumeProfileSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Timeframe = table.Column<string>(type: "text", nullable: false),
                    WindowStartMs = table.Column<long>(type: "bigint", nullable: false),
                    WindowEndMs = table.Column<long>(type: "bigint", nullable: false),
                    PocPrice = table.Column<double>(type: "double precision", nullable: false),
                    VahPrice = table.Column<double>(type: "double precision", nullable: false),
                    ValPrice = table.Column<double>(type: "double precision", nullable: false),
                    ProfileBinsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumeProfileSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SmartMoneyStructures_Symbol_Timeframe_TimeMs",
                table: "SmartMoneyStructures",
                columns: new[] { "Symbol", "Timeframe", "TimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_VolumeProfileSnapshots_Symbol_Timeframe_WindowEndMs",
                table: "VolumeProfileSnapshots",
                columns: new[] { "Symbol", "Timeframe", "WindowEndMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmartMoneyStructures");

            migrationBuilder.DropTable(
                name: "VolumeProfileSnapshots");
        }
    }
}
