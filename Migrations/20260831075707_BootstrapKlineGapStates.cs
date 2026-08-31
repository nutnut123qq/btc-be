using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class BootstrapKlineGapStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL statement_timeout = '10min';

                WITH config("Timeframe", interval_ms) AS (
                    VALUES ('1m', 60000::bigint), ('5m', 300000::bigint), ('15m', 900000::bigint),
                           ('30m', 1800000::bigint), ('1h', 3600000::bigint),
                           ('4h', 14400000::bigint), ('1d', 86400000::bigint)
                ), bounded AS (
                    SELECT k."Symbol", k."Timeframe", k."OpenTimeMs", c.interval_ms,
                           1577836800000::bigint AS audit_start,
                           ((extract(epoch FROM statement_timestamp()) * 1000)::bigint
                               - ((extract(epoch FROM statement_timestamp()) * 1000)::bigint % c.interval_ms)) AS audit_end
                    FROM "Klines" k
                    JOIN config c ON c."Timeframe" = k."Timeframe"
                    WHERE k."Symbol" = 'BTCUSDT' AND k."OpenTimeMs" >= 1577836800000::bigint
                ), ordered AS (
                    SELECT *, lag("OpenTimeMs") OVER (
                        PARTITION BY "Symbol", "Timeframe" ORDER BY "OpenTimeMs") AS previous_open
                    FROM bounded WHERE "OpenTimeMs" <= audit_end
                ), internal_gaps AS (
                    SELECT "Symbol", "Timeframe", previous_open + interval_ms AS gap_start,
                           "OpenTimeMs" - interval_ms AS gap_end,
                           (("OpenTimeMs" - previous_open) / interval_ms - 1)::bigint AS missing
                    FROM ordered
                    WHERE previous_open IS NOT NULL AND "OpenTimeMs" - previous_open > interval_ms
                ), stats AS (
                    SELECT "Symbol", "Timeframe", min("OpenTimeMs") AS minimum_open,
                           max("OpenTimeMs") AS maximum_open, min(interval_ms) AS interval_ms,
                           min(audit_start) AS audit_start, min(audit_end) AS audit_end
                    FROM bounded WHERE "OpenTimeMs" <= audit_end GROUP BY "Symbol", "Timeframe"
                ), edge_gaps AS (
                    SELECT "Symbol", "Timeframe", audit_start AS gap_start,
                           minimum_open - interval_ms AS gap_end,
                           (minimum_open - audit_start) / interval_ms AS missing
                    FROM stats WHERE minimum_open - audit_start >= interval_ms
                    UNION ALL
                    SELECT "Symbol", "Timeframe", maximum_open + interval_ms, audit_end,
                           (audit_end - maximum_open) / interval_ms
                    FROM stats WHERE audit_end - maximum_open >= interval_ms
                ), all_gaps AS (
                    SELECT * FROM internal_gaps UNION ALL SELECT * FROM edge_gaps
                )
                INSERT INTO "KlineGapStates"
                    ("Symbol", "Timeframe", "StartOpenTimeMs", "EndOpenTimeMs", "MissingBars",
                     "AttemptCount", "LastAttemptAtUtc", "NextRetryAtUtc", "Status", "Reason",
                     "FirstDetectedAtUtc", "UpdatedAtUtc")
                SELECT "Symbol", "Timeframe", gap_start, gap_end, missing, 0, NULL, NULL,
                       'Pending', 'BOOTSTRAP_DISCOVERY', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM all_gaps
                ON CONFLICT ("Symbol", "Timeframe", "StartOpenTimeMs", "EndOpenTimeMs") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "KlineGapStates"
                WHERE "Reason" = 'BOOTSTRAP_DISCOVERY' AND "AttemptCount" = 0;
                """);
        }
    }
}
