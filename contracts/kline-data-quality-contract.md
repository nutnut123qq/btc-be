# BTC Kline data-quality and repair contract v1

Scope is fixed to `BTCUSDT` and active `1h`, `4h`, `1d` candles. The issue API
classifies only finalized ranges. A current forming interval is never a gap.

## Stable taxonomy

| Issue type | Cause codes | Resolution states |
|---|---|---|
| `missing_interval` | `absent_from_store`, `upstream_no_data_retry`, `upstream_no_data_confirmed`, `upstream_transport_failure` | `open`, `retry_scheduled`, `source_unavailable` |
| `invalid_duration` | `duration_mismatch`, `close_before_open` | `open` |

Source classifications are `not_checked`, `binance_spot_verified`,
`binance_spot_empty`, `binance_spot_rejected`, and `binance_spot_error`.
`GET /api/market/data-quality/issues` returns the exact affected open-time range,
expected and observed duration, detection/source lineage, lifecycle timestamps,
and downstream artifacts which become stale if the source candle changes.
Physical open-time sequence discovery is not limited by the persistent retry
ledger. Without an explicit `startOpenTimeMs`, head coverage begins at the
earliest stored finalized candle.

## Controlled repair

`POST /api/market/data-quality/repair` is admin-only, defaults to `dryRun=true`,
and is hard-limited to 1,000 aligned candles. Binance Spot `/api/v3/klines` is
the only accepted repair source. Every eligible row must be inside the requested
range, have the exact timeframe duration, be finalized, and have valid OHLCV.

The dry run returns a SHA-256 over the exact source evidence, before-state and
planned actions. Apply requires that exact SHA. Any source or database drift
fails closed. Applied plans persist an immutable receipt and exact before/source
JSON; repeating an already-applied plan is a no-op. Missing source rows remain
explicitly unresolved. OHLCV is never interpolated, forward-filled, averaged,
or copied from another timeframe.

Raw candle repair does not silently rewrite derived artifacts. The response
lists affected tables so their bounded, versioned rebuild workflows can be run
and audited separately.
