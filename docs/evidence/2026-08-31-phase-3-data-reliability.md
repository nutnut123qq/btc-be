# Phase 3 data reliability and performance evidence — 2026-08-31

## Scope and baseline

- Pre-Phase-3 backend HEAD: `231e62a7b7e59ad004692818a485abbd8bc1f083`.
- BTC remains the only initialized reliability ledger in this phase.
- Latest-candle ingestion has a budget independent from historical repair; historical work is persisted and scheduled fairly across timeframes.
- A gap can be `Pending`, `Unavailable`, or `Filled`. Only three real empty Binance responses, spaced at least 24 hours apart, can make it `Unavailable`; transport failures do not count as empty-data evidence.
- Worker status is persisted separately from process liveness, database readiness, and market-data freshness.

## Backup and migrations

- Pre-migration backup: `D:\code\btc-artifacts\phase3\20260831T133322Z\bitcoin_analyst_20260831T133322Z.dump`.
- Archive size: 5,282,029,889 bytes.
- SHA-256: `8f5f3d41450f4ba530ae95639f2ab0b73ae591f461c4bd0f6685481badd4c755`.
- `pg_restore --list`: 499 entries; 36/36 user tables inventoried; secret scan found no match.

Applied migrations:

1. `20260831064801_AddPersistentGapStateAndWorkerHeartbeats`
2. `20260831075707_BootstrapKlineGapStates`

The bootstrap migration is data-only: PostgreSQL `LAG()` discovers BTC internal gaps, leading/trailing gaps are added, and `ON CONFLICT` makes insertion idempotent. It inserted 83 persistent gap states without changing any Kline. The generated transaction uses a stable `statement_timestamp()`, transaction-local server timeout, and a five-minute migration-client timeout; no session setting leaks into the Npgsql pool.

## Current BTC ledger

The post-bootstrap normal audit reconciled all seven timeframes:

| TF | Total Klines | Expected | Missing | Known ranges | Pending | Unavailable | Ledger |
|---|---:|---:|---:|---:|---:|---:|---|
| 1m | 3,498,378 | 3,505,479 | 7,101 | 17 | 17 | 0 | Reconciled |
| 5m | 700,569 | 701,096 | 527 | 16 | 16 | 0 | Reconciled |
| 15m | 233,526 | 233,699 | 173 | 16 | 16 | 0 | Reconciled |
| 30m | 116,766 | 116,850 | 84 | 16 | 16 | 0 | Reconciled |
| 1h | 58,388 | 58,425 | 37 | 16 | 16 | 0 | Reconciled |
| 4h | 14,604 | 14,607 | 3 | 2 | 2 | 0 | Reconciled |
| 1d | 2,435 | 2,435 | 0 | 0 | 0 | 0 | Reconciled |

Expected/missing values advance with the interval clock while total persisted candles remain unchanged. Evidence-bearing or `Unavailable` tails are never extended with new bars; new elapsed bars receive a separate evidence-free transient segment.

## Audit and health performance

The default endpoint is the reliability path: `includeInventory=false`. It uses the persistent ledger plus indexed per-timeframe `MIN/MAX`; six expensive derived-table inventories are nullable and run only with explicit `includeInventory=true`.

HTTP measurements on the migrated database with background workers disabled:

- First cold Data Audit: 979.58 ms.
- Cached Data Audit, 20 calls: p95 11.85 ms, maximum 16.15 ms.
- Database readiness, 20 calls: p95 158.40 ms, maximum 183.80 ms.
- 100 cached audit calls: working-set delta +4.67 MB, with no unbounded growth observed.

Direct production-DI PostgreSQL measurements were also repeated with a fresh audit cache:

- First cold: 801.63 ms.
- Cache-invalidated cold, 20 calls: p95 88.15 ms.
- Cached, 20 calls: p95 0.01 ms.

Existing Kline covering indexes serve latest/min/max seeks; execution-plan review did not justify another Kline index. Database maintenance review identified `FuturesMetrics` dead tuples as a future ordinary `VACUUM/ANALYZE` item, not a Phase-3 schema change.

## Correctness and resilience verification

- Full backend suite: 205 passed, 0 failed.
- Release build: 0 warnings, 0 errors.
- Real PostgreSQL tests cover server-side `LAG()`, unbootstrapped symbols, and a partially initialized symbol where one timeframe is empty; temporary rows are removed in `finally`.
- Empty timeframe reports the full configured range as missing and uses `LiveFallback`; it never appears complete.
- Tail evidence, restart persistence, retry spacing, latest-first budget, transport failure behavior, manual retry, and idempotent insert paths have regression tests.
- EF reports no pending model changes; idempotent migration script generation succeeds.
- NuGet vulnerability scan: no vulnerable direct or transitive package.
- `git diff --check`: no whitespace error; only repository line-ending notices.
- Independent backend review: **FINAL PASS**.

## Runtime degraded-state evidence

- `/api/health/live` remains healthy independently of data age.
- `/api/health/ready` checks the database with a short timeout.
- `/api/health/freshness` truthfully reported stale short timeframes while workers were intentionally disabled for the read-only smoke test.
- `/api/health/workers` reported `never` when no heartbeat existed; it did not invent a success state.
- A nonexistent manual retry returned the structured `GAP_NOT_FOUND` envelope and no raw exception.

## Known limitations

- `LiveFallback` prioritizes truth when the ledger is absent, stale, or only partially initialized. It may exceed the three-second normal-path budget (one empty-ledger measurement was 6.595 seconds) and is surfaced explicitly as degraded.
- `includeInventory=true` is an explicit slow Lab path for exact derived-table counts and is not used by Settings or the reliability dashboard.
- Background workers were kept disabled during browser/performance smoke to avoid unrelated RSS/index/dataset mutations. Real PostgreSQL integration tests and deterministic worker tests cover persistence and scheduling; normal operation will create heartbeats on the next worker cycle.

## Gate result

Phase 3 backend is green. The normal reconciled path exceeds the requested latency margin, restart-safe gap state is populated, data freshness and worker state are separated truthfully, and degraded reconciliation remains correct instead of hiding gaps.
