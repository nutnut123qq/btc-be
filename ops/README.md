# Local production-like operations

These PowerShell scripts own the local three-service workflow from the backend repository. They resolve `backend`, sibling `frontend`, and sibling `ai` from the checkout location; no user-specific path is stored.

## Prerequisites

- Windows PowerShell 7 (`pwsh`), .NET 8, Node.js, and the existing `ai/venv`.
- Native PostgreSQL 17 client tools on `PATH` or under the standard PostgreSQL 17 installation directory.
- Native PostgreSQL is the source of truth. These scripts never start Docker or create a replacement PostgreSQL instance.
- Set `PGDATABASE` and `PGPASSWORD`; optionally set `PGHOST`, `PGPORT`, and `PGUSER`. Keep passwords in the process environment, never in a script.
- For unattended local operation, run `configure-secrets.ps1` once. It stores the database password and generated admin key as a current-user DPAPI-protected CLIXML file under ignored `.ops/`; `show-admin-key.ps1` reveals the admin key only on demand.
- `start.ps1` and `migrate.ps1` derive the backend/EF connection from that validated PG target. They do not accept a second independent database target.

## Build and run

```powershell
pwsh ./ops/build.ps1
pwsh ./ops/configure-secrets.ps1
pwsh ./ops/start.ps1
pwsh ./ops/status.ps1
pwsh ./ops/self-test.ps1
pwsh ./ops/logs.ps1 -Component backend -Stream err -Follow
pwsh ./ops/stop.ps1
pwsh ./ops/watchdog.ps1
pwsh ./ops/run-ai-job.ps1 -Job Futures
pwsh ./ops/run-ai-job.ps1 -Job Paper
```

`build.ps1` publishes the backend, runs `npm ci && npm run build`, and checks the AI virtual environment. `start.ps1` always builds first so changed source, package locks, and configuration cannot run against stale artifacts, then runs the backend with `ProductionLike`, FastAPI without `--reload`, and Next.js with `next start`. Use `start.ps1 -SkipBuild` only for an intentional restart of already verified unchanged artifacts. Runtime files and logs live in ignored `.ops/`. Startup checks PostgreSQL 17 readiness but never starts it. Production-like startup never applies migrations.

## Controlled migration and backup

The backup script exports one PostgreSQL repeatable-read snapshot and uses that same snapshot for exact row counts and `pg_dump`; external workers cannot race the evidence:

```powershell
$env:PGDATABASE = "bitcoin_analyst"
pwsh ./ops/backup.ps1
pwsh ./ops/restore-verify.ps1 -BackupPath ./.ops/backups/bitcoin_analyst_TIMESTAMP.dump -Mode Split
pwsh ./ops/migrate.ps1 -BackupPath ./.ops/backups/bitcoin_analyst_TIMESTAMP.dump
```

Every backup has SHA-256 checksums for the dump and manifest, exact public-table row counts, source server/database identity, retention metadata, and—when present—a separately checksummed archive whose model artifacts are individually checked against their JSON manifests. `migrate.ps1` refuses to run while managed services are active or until the supplied backup and validated PG/EF migration target identities match.

`Split` is a capacity-constrained logical drill: it restores full pre/post schema into one unique empty database, then restores complete pre-data/data without indexes into another and reconciles exact row counts. It does **not** prove that post-data indexes and foreign keys build successfully against restored rows. Both databases are dropped in `finally`.

`Full` is the actual quarterly restore gate because it builds post-data objects against all restored data. Run it only when free space can hold a complete duplicate; the current database needs substantially more than the space used by a split data restore. No verification mode can target the configured source database.

## API contract and CI

```powershell
pwsh ./ops/contract.ps1          # intentionally update contracts/openapi.json
pwsh ./ops/contract.ps1 -Check   # fail on any generated difference
```

Contract generation starts a temporary `ContractGeneration` host with workers disabled and no database migration. The OpenAPI `info.version` must equal `ResearchVersions.ApiContract`. CI restores, builds, runs tests, rejects vulnerable packages, checks pending EF model changes, regenerates OpenAPI, and requires an API contract version increment when the committed contract changes in a pull request.
