using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperTradingEnhancements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Atr14",
                table: "PaperTrades",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BalanceAfter",
                table: "PaperTrades",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnsembleDirection",
                table: "PaperTrades",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExitReason",
                table: "PaperTrades",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PositionSizeUsdt",
                table: "PaperTrades",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StopLossPrice",
                table: "PaperTrades",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TakeProfitPrice",
                table: "PaperTrades",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Atr14",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "BalanceAfter",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "EnsembleDirection",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "ExitReason",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "PositionSizeUsdt",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "StopLossPrice",
                table: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "TakeProfitPrice",
                table: "PaperTrades");
        }
    }
}
