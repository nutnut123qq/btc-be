# Phase 0 baseline evidence — 2026-08-31

## Scope

- Optional Gemini embeddings no longer generate repeated work when no key is configured.
- RAG falls back to recent news without an embedding provider.
- Kline ingestion reserves request budget for current candles and stops only the current attempt on an empty historical response.
- Health exposes independent liveness, database readiness, and BTC data freshness.
- Bitcoin Magazine RSS uses its current working feed URL.

## Verification

- Release build: 0 warnings, 0 errors.
- Backend tests: 146 passed, 0 failed, 0 skipped.
- NuGet vulnerability scan: no vulnerable direct or transitive packages.
- `git diff --check`: no whitespace errors.
- Independent review: no remaining correctness, security, or scope blocker.

## Database snapshot and backup

- PostgreSQL: 17.6.
- Database size at snapshot: 21,757,488,275 bytes.
- User tables inventoried: 36.
- Verified archive: `D:\code\btc-artifacts\phase0\20260831T043036Z\bitcoin_analyst_20260831T043036Z.dump`.
- Archive size: 4,919,626,362 bytes.
- SHA-256: `03eb7f74cace749f5b7860381a46339ae58b9bb7703048b4419294d657cf7c81`.
- `pg_restore --list`: exit 0, 494 entries, empty stderr.
- Credential and connection-string scan: no match.
- Supporting evidence is stored beside the archive in `verification.json`, `SHA256SUMS.txt`, `restore-list.txt`, and CSV snapshots.

The first compressed dump attempt was interrupted by a transient Windows socket-buffer error during `TechnicalIndicators` COPY. Its partial archive is preserved under `D:\code\btc-artifacts\phase0\20260831T040934Z` and is not considered restorable evidence.

## Research truth baseline

- Current ensemble evaluation: 16,613 total predictions, 5,888 true, 10,679 false, 46 pending; directional win rate 35.54%. It is not promotion eligible.
- Current paper sample: 9 closed trades, 66.7% win rate, -1.01% net return, 6.1% maximum drawdown. The sample is too small for a research conclusion.
- Existing historical model and backtest claims remain legacy evidence until rerun with versioned manifests and the promotion gates defined in the roadmap.

## Gate result

Phase 0 is green. This report does not promote any model or paper strategy.
