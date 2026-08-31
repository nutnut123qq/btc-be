# Phase 1 evidence — truthful contracts and optional LLM

Date: 2026-08-31
Pre-commit backend HEAD: `8d80677177861bf63cb7bd6cb531003ae6748077`
Migration: none
Push: not performed

## Scope and review

- Reviewed the complete backend working-tree diff before recording this evidence.
- Changes are limited to Phase 1 controllers, transition/confluence services and DTOs, plus focused contract/error tests.
- No `appsettings`, project file, migration, database schema, or unrelated feature changed.
- Secret scan found no credential. Matches were limited to cancellation-token identifiers and deliberate fake secret strings in negative sanitization tests.
- `git diff --check` reported no whitespace error; Git only reported expected LF-to-CRLF working-copy notices.

## Verification

| Check | Result |
|---|---|
| Release build | PASS — 0 warnings, 0 errors |
| Backend tests | PASS — 166/166 |
| NuGet vulnerability check | PASS — 0 known vulnerabilities |
| Database migration | Not required |

## Runtime contract evidence

- `GET /api/meta` returns the research environment and explicit app, API-contract, data-pipeline, and evaluation versions.
- Transition matrix returns a stable object contract containing symbol, timeframe, window size, archetype count, total transitions, and typed cells with archetype codes.
- Transition prediction, sequence prediction, and entropy output are explicitly marked unvalidated/experimental and include a reason instead of implying predictive evidence.
- Confluence returns typed timeframe alignments and maps both legacy PascalCase and canonical camelCase payloads. Runtime calculation uses a supported window size and preserves regime/archetype fields when a match exists.
- `GET /api/ai-chat/capabilities` proxies AI capability state and advertises the deterministic backend explanation fallback.
- BTC analysis normalizes accepted `BTC`/`BTCUSDT` input to `BTCUSDT` market data. Non-BTC analysis fails closed with `UNSUPPORTED_SYMBOL`.
- AI-service failures are converted to the shared safe error envelope; raw provider bodies, stack traces, and provider exceptions are not forwarded to the UI.
- Chat streaming accepts only a complete structured upstream stream, bounds buffered content, and otherwise uses the deterministic C# explanation.

## Browser smoke

- Archetype transition matrix opened without a full-screen crash.
- Archetype prediction showed `EXPERIMENTAL` and the out-of-sample limitation.
- Advanced confluence displayed typed regime and archetype values where the backend returned a current match.
- Global `LLM OFF` state remained visible while quantitative screens continued to work.
- Paper Journal displayed the fixed `SIMULATION` label and did not describe simulated orders as Binance executions.
- No browser console warning or error was observed while traversing the checked Phase 1 screens.

## Known limitations retained deliberately

- The historical ensemble directional accuracy of approximately `35.54%` remains unvalidated and is not production evidence.
- Transition-derived predictions remain experimental until an out-of-sample promotion evaluation exists.
- Confluence archetype may legitimately be `N/A` for `15m` or `1d` when no current archetype matches; the backend does not fabricate a value.
