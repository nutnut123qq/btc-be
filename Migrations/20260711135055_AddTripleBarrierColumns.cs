using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTripleBarrierColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TargetDirectionTb1d",
                table: "PriceTargets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetDirectionTb1h",
                table: "PriceTargets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetDirectionTb4h",
                table: "PriceTargets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TargetReturnTb1d",
                table: "PriceTargets",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TargetReturnTb1h",
                table: "PriceTargets",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TargetReturnTb4h",
                table: "PriceTargets",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetDirectionTb1d",
                table: "PriceTargets");

            migrationBuilder.DropColumn(
                name: "TargetDirectionTb1h",
                table: "PriceTargets");

            migrationBuilder.DropColumn(
                name: "TargetDirectionTb4h",
                table: "PriceTargets");

            migrationBuilder.DropColumn(
                name: "TargetReturnTb1d",
                table: "PriceTargets");

            migrationBuilder.DropColumn(
                name: "TargetReturnTb1h",
                table: "PriceTargets");

            migrationBuilder.DropColumn(
                name: "TargetReturnTb4h",
                table: "PriceTargets");
        }
    }
}
