using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260323120000_AddPriceAlertSettings")]
public partial class AddPriceAlertSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PriceAlertSettings",
            columns: table => new
            {
                UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                PriceAboveUsd = table.Column<decimal>(type: "numeric", nullable: true),
                PriceBelowUsd = table.Column<decimal>(type: "numeric", nullable: true),
                KlineInterval = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CooldownMinutes = table.Column<int>(type: "integer", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PriceAlertSettings", x => x.UserId);
            });

        migrationBuilder.InsertData(
            table: "PriceAlertSettings",
            columns: new[] { "UserId", "Enabled", "PriceAboveUsd", "PriceBelowUsd", "KlineInterval", "CooldownMinutes", "UpdatedAt" },
            values: new object[,]
            {
                { "default", false, null, null, "1m", 30, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
            },
            columnTypes: new[]
            {
                "character varying(128)", // UserId
                "boolean",                  // Enabled
                "numeric",                  // PriceAboveUsd
                "numeric",                  // PriceBelowUsd
                "character varying(16)",   // KlineInterval
                "integer",                  // CooldownMinutes
                "timestamp with time zone" // UpdatedAt
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PriceAlertSettings");
    }
}
