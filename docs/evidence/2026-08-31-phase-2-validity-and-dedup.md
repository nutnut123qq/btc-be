# Phase 2 validity, legacy isolation, and alert deduplication evidence — 2026-08-31

## Scope and baseline

- Pre-Phase-2 backend HEAD: `f153f3b40efa8df0620e8906726696a8db28c488`.
- Research records now carry pipeline/evaluation versions, `Valid | Legacy | Invalid` status, invalid reason, and archive time.
- Core APIs return only unarchived `Valid` records by default. Lab access is explicit through `includeLegacy=true`.
- Alert source events use nullable deterministic keys, active-row uniqueness, archival retention, and idempotent writes.
- All ensemble evidence in this phase remains `Experimental`, non-validated, and ineligible for promotion.

## Backup gate

- Verified archive: `D:\code\btc-artifacts\phase2\20260831T123219Z\bitcoin_analyst_20260831T123219Z.dump`.
- Archive size: 5,281,781,509 bytes.
- SHA-256: `c470a8e7036613d61ac6f961ad3a2fa14cabee8eedd5ad5a7ed24b66eec70f1`.
- `pg_restore --list`: 494 entries.
- User tables inventoried: 36.
- Credential/secret scan: no match.

## Migrations

The following migrations were applied successfully:

1. `20260831054139_AddResearchValidityAndAlertDeduplication`
2. `20260831060704_AddEnsembleReevaluationLineage`

The first migration defaults pre-existing research rows to `legacy-unversioned`, safely reconstructs only uniquely attributable sequence-alert source keys, archives duplicate active non-null keys deterministically by `CreatedAt, Id`, and then creates the filtered unique index. The second adds immutable ensemble re-evaluation lineage and enforces one record per `(SourcePredictionId, EvaluationVersion)`.

EF reports no pending migrations. A full idempotent migration script was generated successfully.

## Classification dry-run and apply evidence

The pre-apply dry-run classified the existing records as follows:

| Dataset | Total | Valid | Legacy | Invalid |
|---|---:|---:|---:|---:|
| Backtest runs | 6 | 0 | 1 | 5 |
| Model predictions | 3 | 0 | 2 | 1 |
| Ensemble predictions | 16,783 | 0 | 4,353 | 12,430 |

- Five backtests failed ledger/accounting reconciliation; the remaining run is structurally consistent but unversioned.
- One model prediction is a duplicate natural inference event; two structurally valid predictions remain Legacy.
- Ensemble invalid reasons comprise probability normalization, invalid entry price, and duplicate natural source events.
- Applying the classifier produced exactly the proposed counts.
- Re-running both dry-run and apply returned the same ordered result and did not change archive timestamps or counts.
- Core-visible valid research records after classification: 0.

## Alert migration and deduplication evidence

- Total alerts inspected: 95.
- Safely reconstructed deterministic source keys: 76.
- Alerts intentionally left with `SourceKey = null`: 19.
- Duplicate active non-null source keys after migration: 0.
- Alerts archived by migration deduplication: 0.
- Re-running the deduplication operation produced zero candidates and zero mutations.
- Null-key alerts were never deduplicated from title/message/price similarity, avoiding the known false-positive pair from distinct trigger candles.

## Ensemble evidence preservation and re-evaluation

- Raw legacy baseline, all symbols: 16,783 records; 5,888 true, 10,679 false, 216 pending; 35.5405% directional accuracy.
- The raw legacy rows are immutable and remain queryable; re-evaluation does not overwrite or hide the 35.5405% result.
- BTC endpoint raw baseline: 16,614 records; displayed accuracy 35.54%.
- BTC canonical structurally valid natural events: 4,330 evaluated; 34.23% accuracy.
- Versioned `evaluation-v2` BTC lineage: 4,331 child records; 34.22% accuracy.
- Re-running `evaluation-v2` created no additional child rows; lineage uniqueness and the retry path are idempotent.
- Raw, canonical, and versioned metrics are all labeled `Experimental`, `validated: false`, and `promotionEligible: false`.

## Verification

- Release build: 0 warnings, 0 errors.
- Backend tests: 179 passed, 0 failed, 0 skipped.
- NuGet vulnerability scan: no vulnerable direct or transitive packages.
- `git diff --check`: no whitespace errors.
- EF migration check: no pending migrations.
- Idempotent EF migration script: generated successfully.
- Browser smoke test with AI `LLM_PROVIDER=none`: Core/Legacy views rendered without uncaught console errors; quant functionality remained available.
- Independent implementation and re-review gates: PASS.

## Known limitation

Artifact existence, manifest linkage, and SHA-256 compatibility are not yet enforceable for historical model records. That evidence is deliberately deferred to Phase 5; no model is promoted before those checks exist.

## Gate result

Phase 2 backend gate is green. Legacy and invalid evidence remains auditable in Lab, Core contains no falsely validated signal, raw ensemble performance remains visible, and alert deduplication is fail-closed for uncertain historical events.
