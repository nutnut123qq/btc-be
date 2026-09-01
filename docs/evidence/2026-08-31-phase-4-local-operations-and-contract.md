# Phase 4 evidence — local operations and API contract

Date: 2026-08-31

## Implemented

- Backend-owned `ops/` workflow for build, start, stop, status, logs, controlled migration, snapshot backup, restore verification, vulnerability checks, OpenAPI checks, and a disposable self-test.
- Production-like startup derives its backend connection from the same validated `PG*` target and requires a native Windows PostgreSQL 17 server/data directory. It never starts Docker/PostgreSQL and never auto-migrates.
- Backup exact row counts and `pg_dump` share one exported repeatable-read snapshot. All database subprocesses are bounded and killed on timeout. Failed runs remove only the exact generated backup artifacts.
- Restore targets use unique validated names, track only databases actually created, preserve cleanup failures, and never target the configured source database.
- Fresh PostgreSQL installs no longer depend on `ai/paper_trader.py` having created `PaperTrades` before EF migrations.
- OpenAPI is committed at `contracts/openapi.json`; SHA-256 is `4AB5D7802C2CC96E0EA7689421FCE0001B7182F7212FDB5F591185777C813A1E`.

## Verification

- Release backend tests: 206/206 passed.
- PostgreSQL-specific focused tests: 6/6 passed.
- Fresh PostgreSQL 17 migration chain: all 32 migrations applied.
- Release build: 0 warnings, 0 errors.
- EF pending-model check: clean.
- NuGet vulnerability scan: 0 findings.
- Generated OpenAPI exact check: passed and deterministic.
- Disposable one-row PostgreSQL drill: snapshot backup, checksum/list, Split restore, Full restore, exact row-count reconciliation, and cleanup all passed.
- Real production-like smoke: native PostgreSQL 17.6 at `D:/PostgreSQL/17/data`, backend/AI/frontend processes verified, all HTTP 200, and process-tree stop left ports 3000/5197/8000 clean.
- In the final degraded-state smoke, AI correctly reported `mlInference=false` and `llmExplanation=false`; the stack remained healthy.

## Gate still open

The quarterly **Full restore of the current production database** has not run. The database is about 20.27 GiB (roughly 12.86 GiB heap/TOAST and 7.40 GiB indexes), while D: has only about 8.25 GiB free. A credible Full restore requires PostgreSQL 17 storage with at least 30–35 GiB free so post-data indexes and constraints build against all restored rows. `Split` is only partial logical evidence and is not represented as a Full restore.

Because this gate is red, Phase 4 is not committed and Phase 5 has not started. Root Compose/PM2/watchdog files remain present but inactive (the related Scheduled Tasks are disabled); they have not been deleted before the replacement workflow and Full restore gate are approved.
