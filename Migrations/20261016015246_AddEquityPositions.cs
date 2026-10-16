using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddEquityPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EquityPositionAccounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AvailableCashVnd = table.Column<decimal>(type: "numeric", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityPositionAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EquityPositionLots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    CostPerShareVnd = table.Column<decimal>(type: "numeric", nullable: false),
                    AcquireDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityPositionLots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EquityPositionAccounts_Symbol",
                table: "EquityPositionAccounts",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EquityPositionLots_Symbol_AcquireDate",
                table: "EquityPositionLots",
                columns: new[] { "Symbol", "AcquireDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EquityPositionAccounts");

            migrationBuilder.DropTable(
                name: "EquityPositionLots");
        }
    }
}
