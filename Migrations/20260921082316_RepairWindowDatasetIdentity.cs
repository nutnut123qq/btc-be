using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class RepairWindowDatasetIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some long-lived local databases were created before the Id
            // identity metadata was repaired in the EF model snapshot. Repair
            // only that physical drift; fresh databases already have an
            // identity sequence and merely receive a safe sequence setval.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    sequence_name text;
                    next_id bigint;
                BEGIN
                    sequence_name := pg_get_serial_sequence('"WindowClassificationDatasets"', 'Id');
                    IF sequence_name IS NULL THEN
                        CREATE SEQUENCE IF NOT EXISTS "WindowClassificationDatasets_Id_seq";
                        ALTER SEQUENCE "WindowClassificationDatasets_Id_seq"
                            OWNED BY "WindowClassificationDatasets"."Id";
                        ALTER TABLE "WindowClassificationDatasets"
                            ALTER COLUMN "Id" SET DEFAULT nextval('"WindowClassificationDatasets_Id_seq"'::regclass);
                        sequence_name := '"WindowClassificationDatasets_Id_seq"';
                    END IF;

                    SELECT COALESCE(MAX("Id"), 0) + 1
                    INTO next_id
                    FROM "WindowClassificationDatasets";
                    PERFORM setval(sequence_name::regclass, next_id, false);
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally irreversible: removing an identity/default from a
            // repaired database would reintroduce data-loss-causing schema drift.
        }
    }
}
