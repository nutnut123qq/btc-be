# Causal SMC storage contract

`CausalSmartMoneyEvents` is the canonical persisted SMC event ledger for BTCUSDT
`1h`, `4h`, and `1d`. `SmartMoneyStructures` remains a legacy/cache table and is
not read or overwritten by the historical rebuild.

## Decision core

The following fields are immutable for an `(Symbol, Timeframe, EventId,
CalculationVersion)` identity: event type, origin/availability/reference times,
prices and zone bounds, segment start, decision source candle list,
`DecisionEvidenceJson`, and `DecisionEvidenceSha256`. The SHA-256 is over the
exact UTF-8 bytes of `DecisionEvidenceJson`. Decision evidence deliberately
excludes later FVG mitigation candles and always records a newly formed FVG as
`active`.

## Lifecycle

`State`, `MitigatedAtMs`, `MitigationSourceOpenTimeMs`, and
`EvaluatedThroughCloseTimeMs` are latest-known lifecycle fields and may advance
idempotently. A historical consumer MUST derive state at cutoff `T` as:

- unavailable when `AvailableTimeMs > T`;
- `mitigated` only when `MitigatedAtMs` is non-null and `MitigatedAtMs <= T`;
- otherwise `active` for FVG or `confirmed` for non-FVG events.

It must never copy the latest `State` into an earlier point-in-time snapshot.

## Gap and source semantics

Only finalized stored Klines are read. A missing interval or a row whose
`CloseTimeMs - OpenTimeMs + 1` differs from the timeframe interval terminates
the segment. Pivot, trend, and active FVG state restart in the next valid
segment. `SegmentStartOpenTimeMs` and the decision source candles prove the
boundary used for an event. `AnalysisCandleCount` is context size, not the
number of defining candles.

## Safe rebuild

`POST /api/smart-money/causal-rebuild` is AdminGuard-protected and defaults to
dry-run. The first applied batch starts at the earliest finalized source candle;
subsequent batches start strictly after the versioned checkpoint. Apply is
atomic, resumable, and bounded by candidate-candle, context-candle, event-count,
and serialized-evidence byte caps. `GET /api/smart-money/causal-coverage` is
read-only and distinguishes historical pending gaps from a trailing interval
that is not yet expected to be finalized.

Before the migration is applied, operators may set `dryRun=true` and
`previewFromBeginning=true`. That mode deliberately bypasses only the absent
checkpoint table and still reads the real finalized Klines/gap ledger; it can
never be used with apply.
