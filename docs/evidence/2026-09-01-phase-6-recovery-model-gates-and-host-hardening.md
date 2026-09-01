# Phase 6 recovery, model gates, and host hardening — 2026-09-01

## Scope and safety boundary

This evidence covers a single-machine, localhost-only, production-like research
deployment. Real-money execution remains disabled. It is not evidence of model
profitability or readiness for public Internet exposure.

## Database recovery evidence

- Fresh backup set: `bitcoin_analyst_20260901T045904Z_21ba56bb`.
- Custom dump size: 5,291,494,219 bytes.
- Dump SHA-256: `18056f42a52195493e389db02ca068f4742902661a697dd0a2e64b9e8868b6fe`.
- Dump listing, dump/manifest/model-archive checksums, and manifest validation passed.
- A complete restore built indexes and foreign keys and matched source row counts
  for all 38 public tables. The verification database was dropped afterward.
- The older verified backup set was removed only after the new set passed. The
  `D:` volume retained approximately 55.44 GiB free after the workflow.

## PostgreSQL and host hardening

- PostgreSQL 17.6 is `Automatic`, running from `D:/PostgreSQL/17/data`, and
  accepts connections on `127.0.0.1:5432`.
- Runtime listeners are restricted to `127.0.0.1` and `::1`; no enabled inbound
  firewall allow rule targets `postgres.exe`.
- The data-root DACL is protected from parent inheritance. Only SYSTEM,
  Administrators, the local operator, and NetworkService have FullControl; the
  owner is NetworkService.
- Recursive ACL audits processed 5,833 entries without an error and found no
  Authenticated Users or BUILTIN Users grants.
- An interrupted first ACL-hardening attempt stopped PostgreSQL and left a child
  ACL unreadable. The elevated recovery took ownership of only the exact data
  tree, reset children from the protected allowlist, returned ownership to
  NetworkService, and passed readiness/listener/firewall checks. No SQL mutation
  was performed by the repair, and the independently restored backup above was
  already available before repair.

## Scheduler and runtime verification

- All four Windows Scheduled Tasks use the absolute inbox Windows PowerShell 5.1
  path rather than relying on a scheduler `PATH` entry for `pwsh.exe`.
- The scheduler-to-PowerShell-to-DPAPI-to-native-process path passed three rounds.
- Database backup, watchdog, futures collector, and paper trader tasks are
  Enabled and Ready with `LastTaskResult = 0x00000000`. Watchdog, futures, and
  paper jobs also completed live post-recovery runs with result zero.
- Native PostgreSQL, backend, AI, and frontend passed a clean production build
  and cold start with four-of-four readiness checks.
- Twelve live API smoke requests returned HTTP 200. All seven BTCUSDT candle
  timeframes reported `fresh`; KlinesIngestionWorker and
  IndexingBackgroundWorker reported `healthy` after successful cycles.
- A live browser traversed all ten application screens with real local APIs. No
  error boundary, API-contract mismatch, page error, or console error appeared.

## Model truthfulness and promotion policy

- BTCUSDT, ETHUSDT, and SOLUSDT artifacts are quarantined. Capabilities report
  `mlInference=false`; prediction requests fail closed with a structured 503.
- The previous flow fitted a calibrator on Train+Validation and then scored that
  same validation data. The corrected flow is Train -> Calibration -> Gate ->
  OOS, with a five-bar purge between adjacent temporal windows.
- The corrected BTC candidate was rejected: OOS accuracy 75.14% versus a 76.80%
  majority baseline, macro F1 0.2887, balanced accuracy 0.3261, MCC 0.0344, and
  zero F1 for both directional classes.
- Nine-fold walk-forward mean macro F1 was 0.4108, but two folds were below 0.40
  and the recent independent window collapsed. This does not satisfy promotion.
- All 14,559 BTC labels match close-to-close `TargetDirection4h`; the documented
  triple-barrier source is null for this dataset. Manifests now require explicit
  dataset and label provenance.
- Promotion requires both independent windows to pass sample/class support,
  macro F1, balanced accuracy, MCC, per-class F1, ECE, and Brier/log-loss
  improvement over a training-prior baseline. Serving independently rechecks
  provenance, checksums, schema, runtime versions, class mapping, and a passing
  promotion gate.

## Final quality gates

- Backend: 206/206 tests in three rounds; Release build zero warnings/errors;
  NuGet vulnerability scan clean; no pending EF model changes; 90,108-byte
  idempotent migration script generated.
- AI: 42 tests plus four subtests in three rounds; `pip check`, `compileall`, and
  diff checks clean.
- Frontend: 26/26 tests in three rounds; lint, typecheck, pinned OpenAPI contract,
  production build, and production dependency audit clean.
- Playwright production/degraded smoke: 2/2 tests in three rounds.
- Diff-focused OWASP review found no new hardcoded secret, unbounded process,
  shell injection, path traversal, unsafe artifact fallback, or fail-open model
  execution. Native arguments and protected-secret conversion have dedicated
  PowerShell 5.1/7 compatibility coverage.

## Remaining non-negotiable limitation

Operational safety is production-like for this one local machine, but predictive
performance is not production-ready. Keeping all ML artifacts quarantined and
real-money execution disabled is the correct passing state until new data and a
new candidate independently satisfy the promotion policy.
