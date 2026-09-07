using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class RepairNewsChunksEmbeddedAtSchemaDrift : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "NewsChunks"
                ADD COLUMN IF NOT EXISTS "EmbeddedAt" timestamp with time zone NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally irreversible: healthy databases already owned this column
            // through InitialCreate, so dropping it would recreate the schema drift.
        }
    }
}
