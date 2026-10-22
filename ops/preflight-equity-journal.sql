-- Preflight for migration EquityResearchSessionLane (EQX v2 A2). READ-ONLY: lists rows that would violate the
-- tightened journal constraints. The migration never edits or deletes data to make itself pass — every row
-- listed here must be investigated and resolved explicitly before the migration is applied.
-- Usage: psql -v ON_ERROR_STOP=1 -At -f ops/preflight-equity-journal.sql   (expect every count = 0)

-- MbbSignalObservations: partially-set fill block (used to pass because NULL comparisons evaluate to NULL).
SELECT 'mbb-observation-fill-partial' AS check, count(*) AS violations
FROM "MbbSignalObservations"
WHERE NOT (("FillPrice" IS NULL AND "FillObservedAtUtc" IS NULL AND "FillSource" IS NULL AND "FillBarOpenTimeMs" IS NULL)
        OR ("FillPrice" IS NOT NULL AND "FillObservedAtUtc" IS NOT NULL AND "FillBarOpenTimeMs" IS NOT NULL
            AND "Decision" IN ('buy-more','reduce','sell','conditional-sell') AND "FillPrice" > 0
            AND "FillObservedAtUtc" >= "ObservedAtUtc" AND NULLIF(BTRIM("FillSource"), '') IS NOT NULL
            AND "FillBarOpenTimeMs" >= "AvailableTimeMs"));

-- MbbSignalObservations: partially-set outcome block.
SELECT 'mbb-observation-outcome-partial' AS check, count(*) AS violations
FROM "MbbSignalObservations"
WHERE NOT (("OutcomeReturn" IS NULL AND "OutcomeObservedAtUtc" IS NULL AND "OutcomeHorizon" IS NULL)
        OR ("FillPrice" IS NOT NULL AND "FillObservedAtUtc" IS NOT NULL AND "OutcomeReturn" IS NOT NULL
            AND "OutcomeObservedAtUtc" IS NOT NULL AND "OutcomeObservedAtUtc" >= "FillObservedAtUtc"
            AND NULLIF(BTRIM("OutcomeHorizon"), '') IS NOT NULL));

-- Detail rows (empty when clean) so an operator can see exactly what blocks the migration.
SELECT 'detail' AS check, "Id", "DecisionId", "Symbol", "Timeframe", "Decision",
       "FillPrice", "FillObservedAtUtc", "FillSource", "FillBarOpenTimeMs",
       "OutcomeReturn", "OutcomeObservedAtUtc", "OutcomeHorizon"
FROM "MbbSignalObservations"
WHERE NOT (("FillPrice" IS NULL AND "FillObservedAtUtc" IS NULL AND "FillSource" IS NULL AND "FillBarOpenTimeMs" IS NULL)
        OR ("FillPrice" IS NOT NULL AND "FillObservedAtUtc" IS NOT NULL AND "FillBarOpenTimeMs" IS NOT NULL
            AND "Decision" IN ('buy-more','reduce','sell','conditional-sell') AND "FillPrice" > 0
            AND "FillObservedAtUtc" >= "ObservedAtUtc" AND NULLIF(BTRIM("FillSource"), '') IS NOT NULL
            AND "FillBarOpenTimeMs" >= "AvailableTimeMs"))
   OR NOT (("OutcomeReturn" IS NULL AND "OutcomeObservedAtUtc" IS NULL AND "OutcomeHorizon" IS NULL)
        OR ("FillPrice" IS NOT NULL AND "FillObservedAtUtc" IS NOT NULL AND "OutcomeReturn" IS NOT NULL
            AND "OutcomeObservedAtUtc" IS NOT NULL AND "OutcomeObservedAtUtc" >= "FillObservedAtUtc"
            AND NULLIF(BTRIM("OutcomeHorizon"), '') IS NOT NULL));

-- Legacy per-symbol cash that the shared pool will NOT absorb automatically (informational, not blocking):
-- after migration the shared pool starts empty (cash-unknown) until the owner enters it explicitly.
SELECT 'legacy-per-symbol-cash' AS check, "Symbol", "AvailableCashVnd"
FROM "EquityPositionAccounts" WHERE "AvailableCashVnd" <> 0;
