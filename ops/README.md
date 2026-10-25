# Local production-like operations

These PowerShell scripts own the local three-service workflow from the backend repository. They support both the monorepo layout (`backend`, `frontend`, `ai`) and sibling deployment repositories (`btc-be`, `btc-fe`, `btc-ai`); no user-specific path is stored.

## Prerequisites

- Windows PowerShell 5.1 or PowerShell 7, .NET 8, Node.js, and the existing `ai/venv`.
- Native PostgreSQL 17 client tools on `PATH` or under the standard PostgreSQL 17 installation directory.
- Native PostgreSQL is the source of truth. These scripts never start Docker or create a replacement PostgreSQL instance.
- Set `PGDATABASE` and `PGPASSWORD`; optionally set `PGHOST`, `PGPORT`, and `PGUSER`. Keep passwords in the process environment, never in a script.
- For unattended local operation, run `configure-secrets.ps1` once. It stores the database password and generated admin key as a current-user DPAPI-protected CLIXML file under ignored `.ops/`; `show-admin-key.ps1` reveals the admin key only on demand. Run `configure-secrets.ps1 -PromptForGeminiApiKey` to add or replace the optional Gemini embedding key without echoing it.
- To enable Gemini explanations with an existing protected key, run `configure-secrets.ps1 -LlmProvider gemini -GeminiModel gemini-3.8-flash`; the AI child receives the key as `GOOGLE_API_KEY` without writing it to a command line or log.
- `start.ps1` and `migrate.ps1` derive the backend/EF connection from that validated PG target. They do not accept a second independent database target.
- Windows Scheduled Task actions should use the absolute inbox shell path `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`; a bare `pwsh.exe` is not available on every host or scheduler PATH.

## Build and run

```powershell
pwsh ./ops/build.ps1
pwsh ./ops/configure-secrets.ps1
pwsh ./ops/rotate-db-password.ps1
pwsh ./ops/start.ps1
pwsh ./ops/status.ps1
pwsh ./ops/common-self-test.ps1
pwsh ./ops/self-test.ps1
pwsh ./ops/logs.ps1 -Component backend -Stream err -Follow
pwsh ./ops/stop.ps1
pwsh ./ops/watchdog.ps1
pwsh ./ops/run-ai-job.ps1 -Job Futures
pwsh ./ops/run-ai-job.ps1 -Job Paper
pwsh ./ops/run-ai-job.ps1 -Job PaperObserver
pwsh ./ops/run-ai-job.ps1 -Job EnsemblePaper
pwsh ./ops/run-ai-job.ps1 -Job Liquidation
pwsh ./ops/run-ai-job.ps1 -Job Sentiment
pwsh ./ops/run-ai-job.ps1 -Job Confluence
```

`build.ps1` publishes the backend, runs `npm ci && npm run build`, and checks the AI virtual environment. `start.ps1` always builds first so changed source, package locks, and configuration cannot run against stale artifacts, then runs the backend with `ProductionLike`, FastAPI without `--reload`, and Next.js with `next start`. Use `start.ps1 -SkipBuild` only for an intentional restart of already verified unchanged artifacts. Runtime files and logs live in ignored `.ops/`. Startup checks PostgreSQL 17 readiness but never starts it. Production-like startup never applies migrations.

`start.ps1` passes the equity document pipeline paths from the workspace layout (`EquityDocuments__PythonPath`, `__PipelineScript`, `__RootPath` = `<workspace>/data/equity-documents`, `EquityFacts__MetricCatalogPath`). Host-only, non-secret switches go in the ignored `.ops/backend-settings.json` as a flat object, e.g. `{"MbbIngestion__Enabled": "true", "EquityIngestion__Enabled": "true", "EquityDocuments__PipelineEnabled": "true", "EquityDocuments__ExtraEnv__EQX_OCR_THREADS": "2"}`; committed defaults keep every equity writer off. Keys that look like secrets or connection strings are refused.

Background workers: `BackgroundWorkers__Enabled` is the master gate; under it `BackgroundWorkers__<Worker>__Enabled=false` leaves that one hosted worker unregistered (switchable: `KlinesIngestionWorker`, `IndexingBackgroundWorker`, `CausalSmartMoneyRebuildWorker`, `RssIngestionService`, `EmbeddingBackfillWorker`, `MlDatasetBuilder`, `WindowDatasetBuilder`; default true). `/api/health/workers` reports such a worker, or every worker when the gate is off, as `disabled` with the configuration key. `EquityDocuments:WorkerStandalone` keeps working with the gate off.

`start.ps1 -RequireHostProfile` (used by the unattended watchdog) refuses to start unless `.ops/backend-settings.json` exists and sets `true`/`false` explicitly for the gate and, when the gate is on, for every worker above plus `Alerts__WorkerEnabled`, `BinanceTestnet__StreamEnabled` and `EquityDocuments__PipelineEnabled`; a missing or partial profile never falls back to the all-on defaults. Every `start.ps1` also refuses when the database's EF migration head differs from the code (`ops/migrate.ps1` first), so a watchdog cannot crash-loop a host against an older schema.

Unattended tasks (S4U, no logged-on user) cannot unlock the deploy user's DPAPI `secrets.clixml`. `configure-machine-secrets.ps1` writes `.ops/machine-secrets.bin` (DPAPI LocalMachine, ACL SYSTEM/Administrators full + deploy user read) with only the database connection, admin key and `LLM_PROVIDER` — never paid API keys. `common.ps1` uses it only when the user-scope file cannot be decrypted. Re-run it after `rotate-db-password.ps1` or a `PGDATABASE` change. `install-local-autostart.ps1` (elevated, once per host) registers `BTCServiceWatchdog` (at startup + every 15 min, `watchdog.ps1 -RequireHostProfile`), `BTCPaperTrader` (every 15 min) and the guarded backup (daily, catch-up when missed, no rotation) under that S4U principal and verifies the definitions. Rollback: `Disable-ScheduledTask` on the three tasks (elevated) and start the stack manually.

`run-ai-job.ps1` is the only supported Scheduled Task entrypoint for derived data. `Paper` always invokes `paper_trader.py --mode forward-paper`; each poll writes one idempotent, timestamped decision/abstention and observed live quote for the latest finalized BTC 4h bar it sees. It does not backfill bars missed during downtime. The decision core is immutable; fill and outcome fields remain empty and may mature only once through separately observed events, so these rows are prospective forecast evidence rather than simulated trades or PnL. PostgreSQL rejects backdated recorder clocks and incoherent fill/outcome timestamps. Historical simulation is available only through an explicit interactive `paper_trader.py --mode replay` invocation, and every such row is persisted as replay with non-prospective provenance. Never schedule replay under the `Paper` job name. `EnsemblePaper` manages existing BTC positions through the admin-guarded backend; new Ensemble entries remain fail-closed until a real promotion gate exists. `Confluence` refreshes BTC only; `Liquidation` and `Sentiment` run their Python snapshot engines. Authentication, HTTP, or Python failures terminate with a non-zero exit code so Task Scheduler cannot report a false success.

Schedule `Paper` every 15 minutes. The unique decision id makes repeated polls of the same finalized 4h bar no-ops, while the tighter cadence bounds normal forward quote latency well below the previous hourly schedule.

`PaperObserver` runs `paper_observer.py --once`: for directional (`long`/`short`) forward-paper rows only, it writes a fill from a live Binance spot quote received within 15 minutes after the decision became available, and a gross 4h outcome (`OutcomeHorizon='4h-gross'`, costs not deducted) from a live quote within 15 minutes after fill+4h. Abstentions are never filled; missed windows stay empty forever (no Klines backfill); every write is a conditional, write-once UPDATE bounded by the database clock. It prints one JSON line per pass (errors as JSON with exit 1). Install it as its own task from an elevated session with `pwsh ./ops/install-paper-observer-task.ps1 -Enable` (`BTCPaperObserver`, every 5 minutes, `IgnoreNew`, 4-minute limit). The task runs `run-paper-observer.ps1`, which calls `run-ai-job.ps1 -Job PaperObserver` and appends one line per run (user, session, batch logon, exit code, PowerShell-level error) to `.ops/logs/paperobserver-task.log`. Its principal is the deploy user with an S4U batch logon (no stored password, runs without a logged-on session, Limited token); the installer re-registers the full definition with `-Force`, so an upgrade also replaces an older principal, and it fails if the resulting task differs from the intended definition. `run-paper-observer.ps1` does not dot-source `common.ps1` or call `run-ai-job.ps1` (both decrypt the user-scope DPAPI `.ops/secrets.clixml`, which needs the deploy user's loaded master key). It reads only `.ops/observer-db.secret`: the PostgreSQL password, DPAPI LocalMachine scope, ACL SYSTEM/Administrators full + deploy user read. Any local process can call LocalMachine decryption, so that ACL is the boundary. Create or refresh it elevated with `pwsh ./ops/configure-observer-secret.ps1` (copies only DB_PASS from the existing protected secrets; prints nothing). A missing, unreadable or malformed secret exits non-zero with a log line; there is no fallback to `ai/.env`. `pwsh ./ops/paper-observer-self-test.ps1` checks the loader and entrypoint without touching real secrets. S4U tasks have no Windows-authenticated network access; the observer only needs local PostgreSQL password auth and anonymous HTTPS. Disable the task with `Disable-ScheduledTask BTCPaperObserver`, and before `rotate-db-password.ps1`; after a password rotation re-run `configure-observer-secret.ps1`.

### PostgreSQL service

`install-postgres-service.ps1` (elevated) prepares, but never starts, the Windows service `postgresql-btc-17` for the existing PostgreSQL 17 cluster: `pg_ctl register` with the same data directory and config, account `NT AUTHORITY\NetworkService` (built-in, no password, not an administrator; the per-service virtual account was rejected by this Windows 10 Pro host), Automatic start, two finite restarts on failure, Modify on the data directory and Read/Execute on the PostgreSQL install only. Switch-over, with the watchdog and the old starters disabled first so two postmasters can never run on one cluster: disable `BTC_AI_Server_Watchdog` and `BTC_Postgres_AutoStart`, move any Startup-folder PostgreSQL shortcut aside, `pg_ctl stop -D <data> -m fast -w`, confirm no `postgres.exe` and port 5432 closed, `Start-Service postgresql-btc-17`, verify version/data_directory/database, then re-enable the watchdog only. Rollback: `Stop-Service postgresql-btc-17`, confirm it stopped, `pg_ctl unregister -N postgresql-btc-17`, re-enable `BTC_Postgres_AutoStart` (it starts the session-bound postmaster while the deploy user is logged on) and restore the shortcut if needed. Never delete `postmaster.pid` while a postmaster may be alive.

`rotate-db-password.ps1` generates a new random PostgreSQL password, stores it only in the current deploy user's DPAPI-protected secret file, changes the configured PostgreSQL role, and verifies a fresh login. Stop the managed stack and disable application Scheduled Tasks before running it. The password is never printed or passed on a process command line.

## Controlled migration and backup

The backup script exports one PostgreSQL repeatable-read snapshot and uses that same snapshot for exact row counts and `pg_dump`; external workers cannot race the evidence:

```powershell
$env:PGDATABASE = "bitcoin_analyst"
pwsh ./ops/backup.ps1
pwsh ./ops/restore-verify.ps1 -BackupPath ./.ops/backups/bitcoin_analyst_TIMESTAMP.dump -Mode Split
pwsh ./ops/migrate.ps1 -BackupPath ./.ops/backups/bitcoin_analyst_TIMESTAMP.dump
```

Production scheduling must use the guarded wrapper. It reserves enough free space
to keep the configured disk floor, verifies the new dump before rotation, retains
at least two complete backup sets, and refuses overlapping runs:

```powershell
pwsh ./ops/guarded-backup.ps1 -PreflightOnly
pwsh ./ops/guarded-backup.ps1 -RetentionCount 2 -MinimumFreeGiB 15
pwsh ./ops/install-backup-task.ps1 -RetentionCount 2 -MinimumFreeGiB 15 -DailyAt "02:00" -Enable
```

`-RetentionCount 0` disables rotation entirely (every verified set is kept; the run fails closed at the free-space floor instead of deleting). Under Windows PowerShell 5.1 pass `-OutputDirectory` explicitly to `install-backup-task.ps1` (`$PSScriptRoot` is empty in parameter defaults there).

The installer updates the existing `Bitcoin Analyst DB Backup` task, preserves its
principal, and leaves it disabled unless `-Enable` is explicit.
Its Scheduled Task action invokes the absolute Windows PowerShell inbox executable
and `run-guarded-backup.ps1`, which bounds runtime, writes a machine-readable atomic
status file at `.ops/status/backup-scheduler-status.json`, and rotates only the exact
`backup-scheduler.log` file. Run `ops/backup-wrapper-self-test.ps1` without touching
the real Scheduled Task or database.

Every backup has SHA-256 checksums for the dump and manifest, exact public-table row counts, source server/database identity, retention metadata, and—when present—a separately checksummed archive whose model artifacts are individually checked against their JSON manifests. `migrate.ps1` refuses to run while managed services are active or until the supplied backup and validated PG/EF migration target identities match.

`Split` is a capacity-constrained logical drill: it restores full pre/post schema into one unique empty database, then restores complete pre-data/data without indexes into another and reconciles exact row counts. It does **not** prove that post-data indexes and foreign keys build successfully against restored rows. Both databases are dropped in `finally`.

`Full` is the actual quarterly restore gate because it builds post-data objects against all restored data. Run it only when free space can hold a complete duplicate; the current database needs substantially more than the space used by a split data restore. No verification mode can target the configured source database.

## API contract and CI

```powershell
pwsh ./ops/contract.ps1          # intentionally update contracts/openapi.json
pwsh ./ops/contract.ps1 -Check   # fail on any generated difference
```

Contract generation starts a temporary `ContractGeneration` host with workers disabled and no database migration. The OpenAPI `info.version` must equal `ResearchVersions.ApiContract`. CI restores, builds, runs tests, rejects vulnerable packages, checks pending EF model changes, regenerates OpenAPI, and requires an API contract version increment when the committed contract changes in a pull request.
