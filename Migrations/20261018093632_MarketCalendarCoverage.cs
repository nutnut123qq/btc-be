using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class MarketCalendarCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MarketCalendarDays is derived reference data (checked-in Data/Calendars file, resynced on startup by
            // MarketCalendarSyncService). Existing rows carry no coverage window and inventing one would be a guess,
            // so they are cleared here and re-created with their real coverage at the next backend start.
            migrationBuilder.Sql(@"DELETE FROM ""MarketCalendarDays"";");

            migrationBuilder.AddColumn<DateOnly>(
                name: "CoverageFromDate",
                table: "MarketCalendarDays",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<DateOnly>(
                name: "CoverageToDate",
                table: "MarketCalendarDays",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddCheckConstraint(
                name: "CK_MarketCalendarDays_Coverage",
                table: "MarketCalendarDays",
                sql: "\"CoverageFromDate\" <= \"Date\" AND \"Date\" <= \"CoverageToDate\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MarketCalendarDays_Coverage",
                table: "MarketCalendarDays");

            migrationBuilder.DropColumn(
                name: "CoverageFromDate",
                table: "MarketCalendarDays");

            migrationBuilder.DropColumn(
                name: "CoverageToDate",
                table: "MarketCalendarDays");
        }
    }
}
