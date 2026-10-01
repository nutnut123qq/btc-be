# Technical-analysis overhaul evidence — 2026-09-22

Status: implemented and deployed locally for BTCUSDT technical research only. Prediction, news and live-trading promotion were explicitly outside this change.

## Acceptance criteria

- One canonical point-in-time technical surface for BTCUSDT 1h/4h/1d.
- Historical Smart Money Concepts (SMC) are causal, gap-aware, versioned and reproducible from finalized candles.
- Technical evidence retains coverage, exclusions, missingness, overlap/dependence, negative results and no-sample cells instead of ranking a winner.
- Database mutation is preceded by a verified backup and bounded dry-run.
- Production jobs are finite, observable and scheduled through reviewed absolute paths.
- Backend, frontend and AI contracts/tests pass before the stack is returned to worker-enabled operation.

## Database safety and migration

- Validated target: PostgreSQL 17.6, database `bitcoin_analyst`, data directory `D:/PostgreSQL/17/data`.
- Initial guarded backup run: `ab15201a515d40aea8759750642c62b5`.
- Initial backup: `bitcoin_analyst_20260921T234529Z_5e46cbf8.dump`, 317,457,777 bytes.
- Pre-migration guarded backup run: `b735f03db5b34c8c9c8e9ca69b0e0659`.
- Pre-migration backup: `bitcoin_analyst_20260922T023759Z_2e03320d.dump`, 326,354,613 bytes.
- The dump, manifest, model archive and their checksums passed restore-list verification.
- A disposable Split restore drill passed schema/pre-data restoration, data restoration and exact manifest row-count reconciliation. A Full restore drill was not attempted because the available PostgreSQL volume did not have the documented 30–35 GiB safety headroom.
- Retention kept two verified backup sets and removed the older superseded set only after both retained sets were re-verified.
- Applied migration: `20260921225126_AddCausalSmartMoneyEvidence`.
- Applied migration: `20260922022129_AddKlineDataQualityRepairAudit`.
- EF Core reports no pending model changes after migration.

The first scheduled-wrapper backup attempt failed safely because Windows PowerShell 5.1 did not expose `Get-FileHash`. No partial set was accepted and no prior set was deleted. Hashing was replaced with a .NET SHA-256 helper shared by backup, restore verification and contract checking; the wrapper self-test and the second production backup passed.

## Causal SMC production rebuild

Before writes, a from-beginning dry-run was executed with the hard batch cap of 5,000 candles:

| Timeframe | Candidates | Valid | Invalid duration | Segments | Estimated events | Evidence bytes |
|---|---:|---:|---:|---:|---:|---:|
| 1h | 5,000 | 4,998 | 2 | 6 | 2,518 | 4,014,430 |
| 4h | 5,000 | 4,991 | 9 | 10 | 2,705 | 4,343,056 |
| 1d | 2,455 | 2,455 | 0 | 1 | 1,400 | 2,240,020 |

The apply was finite and checkpointed: 12 batches for 1h, 3 for 4h and 1 for 1d. Final coverage:

| Timeframe | Checkpoint | Processed candles | Persisted/materialized events | Invalid-duration rows |
|---|---|---:|---:|---:|
| 1h | complete | 58,911 | 32,167 | 8 |
| 4h | complete | 14,734 | 8,123 | 9 |
| 1d | complete | 2,455 | 1,400 | 0 |

Legacy `SmartMoneyStructures` was neither read as canonical evidence nor overwritten. Gap and invalid-duration boundaries reset the SMC state. Immutable decision evidence is hash-checked and lifecycle state is reconstructed as of the requested cutoff.

After the final worker-enabled restart, ingestion and causal upkeep advanced the current checkpoints to 58,913 / 14,735 / 2,456 processed candles. `CausalSmartMoneyRebuildWorker`, `KlinesIngestionWorker` and `IndexingBackgroundWorker` all reported healthy heartbeats. The current data audit reported 32 / 1 / 0 missing bars and 8 / 9 / 0 invalid-duration rows for 1h/4h/1d; these quality issues remain visible rather than imputed.

The versioned data-quality audit subsequently classified 23 / 10 / 0 issues for 1h/4h/1d, including 8 / 9 / 0 invalid-duration rows. Seventeen bounded repair dry-runs queried Binance Spot for exact source candles. Every returned source row was rejected because Binance also reported a close time earlier than the timeframe boundary, so no repair plan was applied and zero production candle rows were mutated. The unresolved rows remain visible with source evidence instead of being normalized or interpolated.

## Production descriptive evidence

- Scheduler-wrapper run: `20260921T235226Z_5f6649fa`.
- Duration: 3,096.601 seconds.
- Published run index: `1ef400ca3c9690afb9108fdd394a3b40bdfa679b0691c78439f88c36246c79e0`.
- Golden semantic ledger hash: `9e526822e3f0f325202b4723325c2a1ff08c9021a98904177045c19dec7cdeba`.

| Timeframe | Stored | Eligible | Excluded | Realized at max horizon | Semantic verification |
|---|---:|---:|---:|---:|---|
| 1h | 162,613 | 127,239 | 35,374 | 127,231 | pass |
| 4h | 46,457 | 31,700 | 14,757 | 31,691 | pass |
| 1d | 6,274 | 5,136 | 1,138 | 5,119 | pass |

The published profile schema is `btc-technical-evidence-profiles/v1`. It includes module/year/timeframe/regime breakdowns, horizon return/MFE/MAE summaries, coverage and missingness, pairwise same-close overlap, confluence dependence and explicit negative/no-sample retention. It forbids ranking/winner selection and outcome-threshold/minimum-sample filtering.

Atomic publication kept the previous pointer until all three timeframe bundles passed semantic and hash verification. Retention then kept two valid successful runs and removed four re-hashed unreferenced files, reclaiming 11,133,194 bytes.

## Frontend and API

- The primary Market page mounts one canonical Technical Replay envelope. Chart, indicators, patterns, volume anomaly, regime, SMC, volume profile and descriptive confluence share the same symbol/timeframe/as-of boundary.
- Live ticker/order-book/trades are labeled as realtime context and are not substituted into historical replay.
- Evidence Center exposes causal coverage and bounded dry-run/apply administration for 1h/4h/1d.
- Evidence profiles expose provenance and descriptive limitations without converting historical means into probabilities, win rates or signals.
- Invalid-duration rows remain visible and make data coverage partial rather than silently healthy.
- Frontend and backend pin the same `2026-09-research-evidence-v9` OpenAPI contract.
- Cold-loading the large evidence catalog initially exposed a real integration timeout: concurrent 10-second reads made Data Audit and 1h causal coverage appear unavailable. Read-only evidence requests are now bounded at 60 seconds and coverage requests at 30 seconds; mutation timeouts and fail-closed parsing were not relaxed.

## Verification

- Backend: 369/369 tests passed; Release build passed with zero warnings/errors; OpenAPI contract matched; EF model had no pending changes.
- Frontend: 109/109 tests passed; TypeScript, ESLint, production build and generated-contract check passed.
- AI after the lifecycle and statistical-evidence patches: 207 tests plus 4 subtests passed; Python compile, dependency consistency and diff checks passed.
- Backup wrapper self-test passed in inbox Windows PowerShell.
- The scheduler-wrapper self-test exposed a Windows PowerShell scope bug where `$PSScriptRoot` was lost inside a delayed callback. The wrapper now captures the script path before scheduling the callback; the regression self-test passes.
- `git diff --check` passed in all three repositories (line-ending conversion notices are informational).
- NuGet reported no vulnerable packages from the configured sources; frontend production dependency audit reported zero vulnerabilities.
- Browser smoke test verified all seven replay layers, all three Data Audit/causal coverage cards, the verified pipeline and eight catalog artifacts. A clean browser tab reported no console warnings/errors. At a 390-pixel viewport, Evidence Center had `scrollWidth=375`, so the tested page did not overflow horizontally.

Three warmed/cold mixed production replay measurements over 500 candles:

| Timeframe | Median | Minimum | Maximum |
|---|---:|---:|---:|
| 1h | 1,214 ms | 838 ms | 3,222 ms |
| 4h | 158 ms | 132 ms | 167 ms |
| 1d | 106 ms | 106 ms | 109 ms |

## Scheduled operations

- `Bitcoin Analyst DB Backup`: daily 02:00, absolute inbox PowerShell, guarded wrapper, two verified retained sets, 15 GiB free-space floor.
- `BTCTechnicalDescriptiveEvidence`: daily 02:20, absolute inbox PowerShell, explicit working directory, finite four-hour limit, atomic status/logs and retention of two successful runs.

Both scheduled actions were inspected after installation. Their historical `LastResult` values were not reset by forcing duplicate production runs; the exact configured wrappers were exercised directly during this deployment.

## Honest limitations

- These artifacts are descriptive historical evidence, not a profitable-alpha, prediction, paper-PnL or live-PnL claim.
- At the stable evidence cutoff, the source retained known missing and invalid-duration observations; they are reported rather than imputed as normal candles.
- The first full causal evidence run exposed a material FVG lifecycle performance hotspot: a linear first-touch scan per event approached quadratic work. It was replaced with a per-contiguous-segment max/min index and leftmost binary descent without changing bootstrap/statistical semantics. Across a production-shaped 56,884-candle fixture and 10,307 indexed queries, index build took 0.478 seconds and all queries took 0.0749 seconds; a bounded 300-query linear reference measured 15.22 ms/query versus 7.27 microseconds/query indexed (about 2,094.7x per-query), with exact parity. The published 51-minute run predates this optimization; the next scheduled run will supply the end-to-end post-optimization duration.
- The current Split restore drill proves logical schema/data restoration and row-count reconciliation, but it does not build post-data indexes and foreign keys against the restored production data. A Full isolated restore remains pending until the PostgreSQL volume has the documented 30–35 GiB free-space headroom.
- No commit or push was performed as part of this deployment.
