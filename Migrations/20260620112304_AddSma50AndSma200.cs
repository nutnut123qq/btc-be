using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSma50AndSma200 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Sma200",
                table: "TechnicalIndicators",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Sma50",
                table: "TechnicalIndicators",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Sma200Dist",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Sma50Dist",
                table: "MlFeatureStores",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sma200",
                table: "TechnicalIndicators");

            migrationBuilder.DropColumn(
                name: "Sma50",
                table: "TechnicalIndicators");

            migrationBuilder.DropColumn(
                name: "Sma200Dist",
                table: "MlFeatureStores");

            migrationBuilder.DropColumn(
                name: "Sma50Dist",
                table: "MlFeatureStores");
        }
    }
}
