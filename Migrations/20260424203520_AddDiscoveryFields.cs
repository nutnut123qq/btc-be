using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoveryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AvgReturn",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "IsAutoDiscovered",
                table: "CandleSequenceRules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SampleCount",
                table: "CandleSequenceRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "WinRate",
                table: "CandleSequenceRules",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvgReturn",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "IsAutoDiscovered",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "SampleCount",
                table: "CandleSequenceRules");

            migrationBuilder.DropColumn(
                name: "WinRate",
                table: "CandleSequenceRules");
        }
    }
}
