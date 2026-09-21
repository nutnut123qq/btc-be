using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddDerivativeAvailabilityLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AvailableTimeMs",
                table: "MarketMetrics",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReconstructed",
                table: "MarketMetrics",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketType",
                table: "MarketMetrics",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReceivedAtUtc",
                table: "MarketMetrics",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "MarketMetrics",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceEventTimeMs",
                table: "MarketMetrics",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AvailableTimeMs",
                table: "FuturesMetrics",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReconstructed",
                table: "FuturesMetrics",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketType",
                table: "FuturesMetrics",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReceivedAtUtc",
                table: "FuturesMetrics",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "FuturesMetrics",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceEventTimeMs",
                table: "FuturesMetrics",
                type: "bigint",
                nullable: true);

            // Event time is known from the legacy key. Original receipt and
            // availability are not known and deliberately remain null.
            migrationBuilder.Sql("""
                UPDATE "FuturesMetrics"
                SET "SourceEventTimeMs" = "OpenTimeMs",
                    "Source" = 'legacy-unknown',
                    "MarketType" = 'usd-m-perpetual',
                    "IsReconstructed" = TRUE
                WHERE "ReceivedAtUtc" IS NULL;

                UPDATE "MarketMetrics"
                SET "SourceEventTimeMs" = "OpenTimeMs",
                    "Source" = 'legacy-unknown',
                    "MarketType" = 'usd-m-perpetual',
                    "IsReconstructed" = TRUE
                WHERE "ReceivedAtUtc" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MarketMetrics_Symbol_Timeframe_AvailableTimeMs",
                table: "MarketMetrics",
                columns: new[] { "Symbol", "Timeframe", "AvailableTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_FuturesMetrics_Symbol_AvailableTimeMs",
                table: "FuturesMetrics",
                columns: new[] { "Symbol", "AvailableTimeMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketMetrics_Symbol_Timeframe_AvailableTimeMs",
                table: "MarketMetrics");

            migrationBuilder.DropIndex(
                name: "IX_FuturesMetrics_Symbol_AvailableTimeMs",
                table: "FuturesMetrics");

            migrationBuilder.DropColumn(
                name: "AvailableTimeMs",
                table: "MarketMetrics");

            migrationBuilder.DropColumn(
                name: "IsReconstructed",
                table: "MarketMetrics");

            migrationBuilder.DropColumn(
                name: "MarketType",
                table: "MarketMetrics");

            migrationBuilder.DropColumn(
                name: "ReceivedAtUtc",
                table: "MarketMetrics");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "MarketMetrics");

            migrationBuilder.DropColumn(
                name: "SourceEventTimeMs",
                table: "MarketMetrics");

            migrationBuilder.DropColumn(
                name: "AvailableTimeMs",
                table: "FuturesMetrics");

            migrationBuilder.DropColumn(
                name: "IsReconstructed",
                table: "FuturesMetrics");

            migrationBuilder.DropColumn(
                name: "MarketType",
                table: "FuturesMetrics");

            migrationBuilder.DropColumn(
                name: "ReceivedAtUtc",
                table: "FuturesMetrics");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "FuturesMetrics");

            migrationBuilder.DropColumn(
                name: "SourceEventTimeMs",
                table: "FuturesMetrics");
        }
    }
}
