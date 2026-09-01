# Phase 5 local production-readiness evidence — 2026-09-01

## Scope and safety boundary

This release is verified for a single-machine, localhost-only, production-like deployment. Real-money execution remains disabled. It is not evidence for a public Internet deployment or for profitable trading.

## Runtime verification

- PostgreSQL 17.6 uses `D:/PostgreSQL/17/data`; local password authentication is SCRAM.
- Backend, AI, and frontend bind to `127.0.0.1` and passed cold-start readiness.
- The watchdog recovered all three managed processes from a fully stopped state and returned exit code 0.
- `KlinesIngestionWorker` and `IndexingBackgroundWorker` completed healthy cycles.
- BTCUSDT candles were fresh for 1m, 5m, 15m, 30m, 1h, 4h, and 1d after catch-up.
- Browser E2E visited all ten application screens three times without console errors or an error boundary.
- Eleven live API smoke endpoints returned HTTP 200. Admin mutation checks returned 401 for missing/wrong credentials and 200 for the protected local key.

## Automation and recovery

- The daily database backup, 15-minute watchdog, 30-minute futures collector, and hourly paper trader tasks are enabled and `Ready`.
- Runtime secrets are stored in a current-user DPAPI-protected CLIXML file excluded from Git; its ACL permits only the current user and SYSTEM.
- The latest 4.92 GB custom PostgreSQL dump has SHA-256 sidecars, exact table counts, a model archive, and a checksummed manifest.
- Dump listing, dump/model checksums, a split logical restore drill, and a tiny full restore drill passed. A full restore of the 20+ GB production database was not run because the disk does not have enough free space for a second full copy.

## Model truthfulness

- BTCUSDT uses the retrained, hashed, schema-pinned artifact `BTCUSDT_4h_ws5_h4h_XGB_v20260901025507.joblib`.
- BTC test metrics: 181 samples, accuracy 74.03%, Brier score 0.3906, macro F1 0.2863. These are research metrics, not a trading-performance guarantee.
- ETHUSDT and SOLUSDT artifacts remain quarantined because their local provenance cannot be proven.
- The paper trader now fails closed when a promoted compatible ML artifact is unavailable; it cannot fall back to quarantined `.joblib` files.
- Legacy ensemble re-evaluation produced 4,334 evaluated records at 34.24% win rate. It remains `Experimental`, unvalidated, and ineligible for promotion.

## Final quality gates

- Backend tests: three clean rounds; Release build: 0 warnings and 0 errors; NuGet vulnerable-package scan: none.
- AI tests: three clean rounds, each 38 tests plus 4 subtests; `pip check`: clean.
- Frontend tests: three clean rounds, each 26 tests; lint, typecheck, pinned OpenAPI contract, and production build: clean.
- Playwright production smoke tests: three clean rounds, each 2/2 passed; npm production audit: 0 vulnerabilities.

## Known operational constraint

`postgresql.conf` is restricted to `listen_addresses = 'localhost'`, but applying that listener change requires an Administrator service restart. Until the next elevated restart, `pg_hba.conf` still restricts authentication to loopback addresses, and all application services remain bound to localhost.
