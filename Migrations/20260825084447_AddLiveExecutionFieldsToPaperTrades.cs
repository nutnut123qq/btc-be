using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveExecutionFieldsToPaperTrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientOrderId",
                table: "PaperTrades",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Commission",
                table: "PaperTrades",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommissionAsset",
                table: "PaperTrades",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ExecutedQty",
                table: "PaperTrades",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OrderId",
                table: "PaperTrades",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RealizedPnL",
                table: "PaperTrades",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WalletBalanceSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WalletBalance = table.Column<decimal>(type: "numeric", nullable: false),
                    CrossWalletBalance = table.Column<decimal>(type: "numeric", nullable: false),
                    BalanceChange = table.Column<decimal>(type: "numeric", nullable: false),
                    TotalUnrealizedProfit = table.Column<decimal>(type: "numeric", nullable: false),
                    EventReasonType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PositionAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    UnrealizedPnL = table.Column<decimal>(type: "numeric", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletBalanceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletBalanceSnapshots_Asset_Timestamp",
                table: "WalletBalanceSnapshots",
                columns: new[] { "Asset", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_WalletBalanceSnapshots_Symbol_Timestamp",
                table: "WalletBalanceSnapshots",
                columns: new[] { "Symbol", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletBalanceSnapshots");

            migrationBuilder.DropColumn(
                name: "ClientOrderId",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "Commission",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "CommissionAsset",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "ExecutedQty",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "RealizedPnL",
                table: "PaperTrades");
        }
    }
}
