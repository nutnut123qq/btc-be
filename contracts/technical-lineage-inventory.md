# Technical lineage inventory

Scope: `BTCUSDT` on `1h`, `4h`, and `1d`. The canonical point-in-time producer for every row below is
`Backend.Services.TechnicalReplayLayerService`, reconstructed from finalized, valid-duration, gap-free `Klines`.

| Replay layer | Calculation version | Legacy storage/producer | Canonical consumer rule |
|---|---|---|---|
| `technicalIndicators` | `finite-window-indicator-events-v1` | `TechnicalIndicators` / `TechnicalIndicatorIndexer` | Legacy rows are incomparable and are not read by replay. |
| `candlePatterns` | `causal-candle-shapes-v1` | `CandlePatterns` / `CandlePatternIndexer` | Legacy rows are not trusted for point-in-time lineage. |
| `volumeAnomaly` | `prior-volume-sma-ratio-v1` | `CandleVolumeStats` / `CandleVolumeIndexer` | Current candle is excluded from the prior-volume baseline. |
| `marketRegime` | `six-close-trend-range-volatility-v1` | legacy ADX/ATR/Bollinger regime services | Legacy regimes are explicitly incomparable. |
| `fibonacci` | `confirmed-alternating-pivot-leg-v1` | no canonical legacy table | Unavailable until an alternating pair of confirmed pivots exists. |
| `volumeProfile` | `rolling-typical-price-volume-profile-v1` | `VolumeProfileSnapshots` / legacy uniform allocation | Replay is an OHLCV typical-price estimate and is labelled as such. |
| `confluence` | `same-close-causal-event-vote-v1` | `ConfluenceSnapshots` / legacy score services | Only same-close events from the six reconstructed modules are accepted; no predictive inputs. |

`SmartMoneyStructures` remains a legacy operational table for `/api/smart-money/structures` and `/detect`.
`/api/smart-money/replay` never reads or writes it. Replay SMC uses `smc-causal-v2` directly from finalized
`Klines`; historical canonical technical events may be stored only as sparse, version/hash-keyed
`TechnicalEvidenceRecords`. `TechnicalEvidenceRebuildCheckpoints` records bounded recent coverage and never
claims a complete historical backfill.
