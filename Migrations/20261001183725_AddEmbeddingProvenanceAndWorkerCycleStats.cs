using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingProvenanceAndWorkerCycleStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastCycleAttempted",
                table: "WorkerHeartbeats",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCycleDetail",
                table: "WorkerHeartbeats",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastCycleFailed",
                table: "WorkerHeartbeats",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastCycleRemaining",
                table: "WorkerHeartbeats",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastCycleSkipped",
                table: "WorkerHeartbeats",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastCycleSucceeded",
                table: "WorkerHeartbeats",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingFailureCount",
                table: "NewsChunks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "NewsChunks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCycleAttempted",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "LastCycleDetail",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "LastCycleFailed",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "LastCycleRemaining",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "LastCycleSkipped",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "LastCycleSucceeded",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "EmbeddingFailureCount",
                table: "NewsChunks");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "NewsChunks");
        }
    }
}
