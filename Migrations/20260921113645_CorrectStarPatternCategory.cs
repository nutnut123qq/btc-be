using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class CorrectStarPatternCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "CandlePatterns"
                SET "PatternCategory" = 'Triple'
                WHERE "PatternType" IN ('MorningStar', 'EveningStar')
                  AND "PatternCategory" <> 'Triple';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "CandlePatterns"
                SET "PatternCategory" = 'Double'
                WHERE "PatternType" IN ('MorningStar', 'EveningStar')
                  AND "PatternCategory" = 'Triple';
                """);
        }
    }
}
