using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AllowProvisionalCalendarDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MarketCalendarDays_Verification",
                table: "MarketCalendarDays");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MarketCalendarDays_Verification",
                table: "MarketCalendarDays",
                sql: "(\"Verification\" IN ('verified','provisional') AND \"SourceUrl\" ~ '^https?://') OR \"Verification\" = 'unverified-legacy'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MarketCalendarDays_Verification",
                table: "MarketCalendarDays");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MarketCalendarDays_Verification",
                table: "MarketCalendarDays",
                sql: "(\"Verification\" = 'verified' AND \"SourceUrl\" ~ '^https?://') OR \"Verification\" = 'unverified-legacy'");
        }
    }
}
