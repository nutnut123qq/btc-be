using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadMlFeatureColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FundingRateZscore",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "LongLiquidationUsd",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "OiDeltaPct",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "PcaComponent1",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "PcaComponent2",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "PcaComponent3",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "PcaComponent4",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "PcaComponent5",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "ShortLiquidationUsd",
                table: "MlFeatureStores");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "FundingRateZscore",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LongLiquidationUsd",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OiDeltaPct",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PcaComponent1",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PcaComponent2",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PcaComponent3",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PcaComponent4",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PcaComponent5",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ShortLiquidationUsd",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);
        }
    }
}
