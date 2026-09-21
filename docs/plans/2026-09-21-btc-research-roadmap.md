# BTC research roadmap — 2026-09-21

Status: implementation in progress and authorized on 2026-09-21. Milestones A/B and bounded C/D/E slices have been implemented locally; this document remains the decision framework, not a claim of profitable alpha.

## Implementation checkpoint — 2026-09-21

- Deployed truthful unavailable states for Ensemble/Markov-style predictive claims; heuristic, descriptive and calibrated-probability labels are separated in API/UI.
- Deployed finalized-candle ingestion, numerical indicator fixtures, gap reset, causal SMC availability, idempotent regime/rule writes, data-quality/derivative-lineage audit, and a confirmed derived-data rebuild workflow.
- Rebuilt BTCUSDT 1h/4h/1d technical and ML datasets from finalized candles only. Raw Klines were preserved apart from three identified legacy forming rows removed after checksum-verifying the database backup.
- Deployed Historical Analog v2 and generated corrected negative/inconclusive evidence; the old unsigned representation is not accepted as predictive evidence.
- Added temporal archetype and common ML evaluators with immutable manifests/trial ledgers, prior-only fitting/calibration, causal baselines, block-bootstrap intervals and fail-closed promotion gates. The first evaluator artifact is superseded because its momentum/reversion baselines read the wrong feature offset; it must not be used as evidence. Evaluator v2 records the explicit 35-feature schema, removes non-causal `ActiveRuleCount`, uses 34 causal features per bar, and validates feature order and probability shape. Its integrity-reviewed run used 14,319 source rows, 13,680 identical OOS timestamps and 114 folds. HGB improved Brier from 0.57269 for the rolling-prior baseline to 0.46473 (paired lift 0.10796; Bonferroni interval [0.10093, 0.11563]) and log loss from 0.96769 to 0.78998, so the predeclared predictive-only gate passed. No model artifact, economic, forward-paper or live promotion was performed.
- The ML v2 artifact is internally immutable and hash-valid, but PostgreSQL gained one qualifying historical row under the same cutoff immediately after the run. The live mutable database therefore cannot reproduce that exact dataset byte-for-byte without a persisted snapshot. Future promotion evidence must pin a durable row snapshot or equivalent immutable extract, not only a cutoff and dataset hash.
- Added the ML evidence v3 bundle and executed the full BTCUSDT 4h protocol at cutoff `2026-09-20 16:00:00Z`: 14,320 frozen source rows, 170 causal inputs (5 bars x 34 features), 13,680 row-level OOS predictions and 114 expanding folds. HGB recorded Brier 0.46473 and log loss 0.78998 versus the strongest adaptive 180-row class prior at 0.55097 and 0.93840. The paired Brier lift was 0.08624 and remained positive for predeclared 6/12/24-row block intervals. Price-return, pattern and time ablations were supported within this fixed HGB protocol; the other groups were inconclusive. The bundle stores a content-addressed NPZ snapshot, JSONL predictions, report and manifest, and its semantic verifier recomputed the reported evidence successfully. This run is explicitly `retrospective-selection-aware`: HGB was selected using overlapping v2 OOS history, so `confirmatory=false` and `promotionAllowed=false` regardless of the retrospective gate result.
- Executed the frozen temporal archetype with training labels available through 2025-12-31 and 1,575 later OOS rows. Coverage was 99.17%; Brier improved from 0.525 to 0.512 (lift 0.0131, block-bootstrap 95% CI 0.0049 to 0.0211), while accuracy was effectively flat/slightly lower (64.85% vs 64.98%). Treat archetypes as modest probability evidence, not a directional-accuracy or PnL claim.
- Deployed chronological rule discovery with a fixed trial budget, held-out evidence, Wilson intervals and disabled experimental rules; rejected trials remain in the ledger.
- Evaluated eight technical feature groups on 14,319 BTCUSDT 4h rows with 13,319 identical OOS timestamps across 56 expanding folds. The full logistic model did not beat the rolling-prior baseline on Brier score (0.57714 vs 0.57663; familywise interval crosses zero), so no feature family or model was promoted. Leave-one-group-out comparisons are diagnostic only: time, pattern and volatility helped the full model; price-return, momentum and volume groups hurt this particular logistic specification.
- Added a causal technical-event evaluator for candle patterns, volume anomalies and regimes. The post-migration rerun used 14,732 closed BTCUSDT 4h bars and joined 9,498 pattern events; zero Morning/Evening Star rows needed evaluator-side category correction. Across 30 strata, no event family showed positive or adverse familywise evidence; 17 were inconclusive and the remainder lacked evidence/history. Causal SMC is reported unavailable because all 7,149 stored rows are legacy rather than `smc-causal-v2`.
- Added a versioned technical capability registry and UI. It separates implementation readiness from evidence stage and evidence target, so operational code cannot be mistaken for runtime health, predictive validation, economic validation or forward evidence.
- Added a top-level Evidence Center (`Nghiên cứu`) and fail-closed research-evidence API. It inventories all 20 technical capabilities and maps them to their frontend surfaces, while artifact detail exposes the research question, immutable dataset/cutoff, protocol, baselines, metrics, familywise uncertainty, coverage, per-group/per-baseline findings, provenance, hashes and limitations. Economic replay and forward observations remain separate, honest evidence stages; missing fills/outcomes are not replaced with simulated PnL.
- Unified backtest/replay execution semantics. Historical replay is explicitly not prospective paper evidence. A BTCUSDT 4h forward recorder logs the latest finalized bar observed by each poll, with an immutable decision core and live quote lineage before outcomes. It does not backfill bars missed during downtime. Fill and outcome are nullable/write-once and PnL is never fabricated; current abstentions are not trades and create no PnL claim.
- Corrected Morning Star and Evening Star storage from `Double` to `Triple`; the migration updates existing derived rows without modifying raw candles.
- Live trading remains outside scope. News remains deferred as originally decided.

## Product decision

Build a trustworthy BTC technical research system that records what was knowable at a decision time, describes historical conditional outcomes, and tests whether those outcomes improve predictions and simulated execution. A completed research module may correctly conclude that no useful predictive effect was found.

Scope: BTCUSDT; candle timeframes 1h, 4h, 1d; retain native-cadence BTC derivatives data. News work is deferred. Keep PostgreSQL, ASP.NET Core, FastAPI/Python and Next.js. No new distributed infrastructure, reinforcement learning or broad model search is needed for this roadmap.

Do not equate engineering completion with guaranteed trading profitability. Replace subjective feature scores with acceptance evidence and explicit maturity: descriptive, experimental, validated for a specified task, forward-observed, retired. These are proposed capability states, not replacements for existing record-validity flags.

## Observed starting points

- Backend EnsembleService.PredictEnsembleAsync uses fixed layer inputs and a fixed fallback price; these outputs are not live estimates.
- Historical analog returns_shape mixes small decimal returns with unit-scale unsigned body/wick features. Representation correctness must precede parameter search.
- Historical analog UI/backend defaults use 20,000 lookback bars while the Python evaluator defaults to 5,000.
- Analog roundtrip threshold defaults to 0.15 percent; trading_config.py assumes 0.30 percent roundtrip. Classification thresholds and execution costs require separate, explicit definitions.
- SmartMoneyService marks pivots/FVG at historical candle times before confirmation would have been possible. Consumed swing handling needs deterministic replay tests.
- Rule discovery selects candidates using results from the same historical input. Clustering statistics are descriptive until fitted/evaluated with temporal separation.
- TransitionService explicitly returns unavailable for sequence prediction; the current model registry quarantines the BTC model.
- Data audit, worker heartbeat, record metadata, tests and backup scripts already exist and should be extended rather than replaced.
- Historical results have already informed research choices. A newly selected historical slice is not automatically an untouched blind holdout.
- README/ops descriptions contain stale multi-asset references. Update documentation together with affected implementation work.

## Shared contracts and architecture

Backend owns ingestion, validated technical features/events, timestamps and query APIs. Python owns research evaluation, fitting/calibration and execution simulation. Frontend displays evidence and capability status. Keep one authoritative implementation of each calculation where practical; cross-language duplicates need versioned golden fixtures.

Add a compact research specification and evidence manifest using existing metadata/registry structures where possible:

- instrument/venue/market type; candle timeframe; horizon in bars AND elapsed time;
- feature/label/representation version, source cutoffs and availability rules;
- train/calibration/selection/evaluation intervals and selected parameters;
- data snapshot or reproducible source hash, exact code revision plus dirty-diff hash, dependencies and random seed;
- baseline, cost/fill specification, tested hypothesis, trial count and run result;
- immutable prediction/decision timestamp, input identity, model identity and later-maturing outcome.

Keep raw observation time, receipt time and derived availability time distinct. Historical backfills without original receipt timestamps must be marked reconstructed rather than claimed as true live observations. Join features using availability <= decision time, including higher-timeframe features and sparse funding observations.

Separate spot research from execution instruments. Preserve existing spot candles. A future BTC perpetual long/short experiment requires identified perpetual prices and historical funding; it must not present spot-price simulations as perpetual execution. Adding same-timeframe perpetual data is a proposed bounded extension, not restoration of removed subhour candles.

## Milestone A — truthful outputs and frozen evaluation specification

Priority: immediate; prerequisite for predictive work.

1. Remove fixed market inputs and fixed-price fallbacks from live ensemble output. Return unavailable where real inputs are absent. Preserve existing records with explicit provenance/invalid reason rather than silently rewriting history.
2. Separate heuristic scores, historical frequencies and calibrated probabilities in DTO/UI wording.
3. Define one cost specification with unit-safe bps/percent/fraction conversions and explicit instrument assumptions. Keep classification dead zones separate from deducted execution costs.
4. Freeze a first benchmark: BTC 4h source, next one 4h bar outcome. Keep 1h/1d available for context and later independent evaluation. Existing 1/3/6-bar analog views remain descriptive until individually evaluated.
5. Establish the evaluation ledger before changing models. Current history is development evidence; reserve subsequent unseen data from a recorded freeze time for forward evaluation.

Acceptance: no fixed market estimates emitted by production paths; missing input yields a truthful unavailable state; same run specification is visible in API/UI/evaluator; old and new results cannot be confused.

## Milestone B — point-in-time data and numerical correctness

1. Extend current audit to derivatives and derived tables: completeness per field, staleness, invalid OHLCV, duplicates, source revisions and unexplained gaps. Never fill an unknown value with zero merely to complete a vector.
2. Mark live forming candles separately; research and signal generation consume finalized candles only.
3. Test indicator conventions and convergence: Wilder smoothing, EMA seeds, VWAP session definition, null warmup, zero-range candles and gap policy. Full rebuild, incremental update and streaming restart must agree within documented per-feature tolerances. The current fixed warmup length must be justified numerically, especially EMA200 and cumulative features.
4. Fix SMC event availability (pivot confirmation after two bars, FVG after its final defining bar) and consume each structural level only once per defined event. Preserve visual origin and available_at separately.
5. Make regime/transition and other derived writes idempotent and versioned. Rebuild only affected BTC derived ranges after verification; do not destroy raw history.
6. Plan a full restore drill on capacity sufficient for a duplicate database; measure retention/growth. Checksum/listing alone is not a full restore test.

Proposed operational targets: no invalid rows in accepted research datasets; every gap classified; ingestion/derived staleness visible; finalized-bar arrival p95 within five minutes under healthy connectivity; no duplicate logical rows after retries. Thresholds are project targets to validate against the running host, not published guarantees.

Acceptance: a future-append test cannot change previously finalized features/events with availability <= the historical cutoff; batch/incremental parity fixtures pass; restart/retry preserves row identities and counts.

## Milestone C — a common statistical evaluator

1. Chronological outer walk-forward evaluation. Fit scaler, PCA, clustering, feature selection and model solely in each training interval. Tune/calibrate only inside prior development partitions.
2. Purge training labels whose realization crosses the decision/evaluation boundary. Choose any gap/embargo using actual feature/label intervals and split design; TimeSeriesSplit(gap=...) alone is not a proof against all leakage.
3. Evaluate every candidate on identical timestamps and comparable coverage. Baselines: rolling empirical class probabilities, majority class, simple momentum and a simple reversion rule. Economic baselines additionally include cash and instrument-appropriate buy-and-hold with comparable capital/exposure disclosure.
4. Show sample counts, class balance, coverage/abstention, per-horizon and per-regime results. Use Brier/log loss plus class-wise reliability diagrams for probabilistic forecasts; balanced accuracy/confusion matrices as diagnostics, not sole promotion targets.
5. Compare prediction losses and net returns with paired time-block bootstrap intervals; document block size sensitivity. For single-BTC continuous factors, optional time-series Rank IC/HAC regression measures complement, rather than replace, economic evaluation. Cross-sectional IC is inapplicable to a single asset.
6. Record every tried variant, including unsuccessful candidates. Control multiple testing across a declared family; use DSR for appropriately specified return-series experiments with credible trial accounting. PBO is optional when a suitable full candidate-return matrix exists. Neither substitutes for forward evaluation.
7. Store replayable evidence artifacts. Make explicit whether an evaluation period has influenced subsequent decisions.

Acceptance: one command reproduces a candidate and its baselines from the same manifest; changing future data cannot change past predictions; API and evaluator agree on fixtures; inconclusive/negative results are valid outputs. Do not require a positive result to call the evaluator complete.

## Milestone D — improve each technical module against that evaluator

| Module | Bounded work | Acceptance evidence |
|---|---|---|
| Chart / ticker / book | Surface venue, freshness, forming/closed bars, reconnect state. Use snapshot + sequence checks for a maintained local order book. | Simulated disconnect/out-of-order updates cannot display silently trusted stale depth. |
| Indicators / volume anomalies | Numerical parity; a small predeclared feature family (trend, momentum, volatility, volume). Use lagged context and robust seasonal comparisons where justified. | Correctness fixtures plus out-of-sample ablation showing each retained predictive feature's contribution. Pure descriptive indicators may remain without alpha. |
| Classical candle patterns / scenarios | Explicit geometric rules and availability; label strengths as heuristic. Evaluate conditional outcomes against contextual baselines. | Counts, uncertainty and baseline comparisons by horizon; no implied win probability from rule strength. |
| Regime | Keep a deterministic descriptive baseline first; fix repeated transition insertion, test threshold stability and causal regime labels. | Reproducible labels and measurable value as an out-of-sample conditioning feature. |
| Historical Analog | Version returns_shape_v2 with signed body/direction and balanced scaling; compare at most three predeclared simple representations. Fit learned scaling only on training data. Introduce quality-based abstention and neighbors-only-from-past. | Opposite-direction synthetic fixtures separated; poor matches can return no evidence; same representation/ranking in API and evaluator; baseline lift with interval and coverage. |
| Archetype gallery/rankings | Persist training-only scaler/centroids with stable version IDs; assign future windows without refitting that version. Separate descriptive library rebuilds from predictive evaluation. | Future data cannot move historical evaluation cluster assignments; rank by evidence with sample size/uncertainty rather than best in-sample rate. |
| Markov / sequence | Define a reproducible causal state sequence; use smoothing/backoff for sparse transitions. Test first-order transitions before adding second order. | Beats unconditional/first-order appropriate baseline on frozen future windows; otherwise retain descriptive matrix and hide predictive claim. |
| Rules | Fixed candidate budget, chronological validation, costs, overlapping-event handling and complete search ledger. | Selected rules survive held-out evaluation; rejected rules remain documented. |
| SMC | Fix confirmation timestamps, one-time breaks, and deterministic FVG state. Treat it as event geometry, not proof of institutional intent. | Sequential replay equals batch events filtered by available_at; conditional outcome tests determine predictive use. |
| Volume Profile | Keep current OHLCV method explicitly approximate; verify volume conservation and POC/value-area conventions. | Accurate description of estimator; no claim of actual traded-at-price distribution. A trade-backed variant is deferred pending storage budget. |
| Futures metrics | Per-field event/receipt/availability times, source labels, missingness, safe as-of joins, funding and mark/spot distinction. | Reproducible raw-to-feature lineage and stale-data exclusion. |
| Liquidation heatmap | Version leverage/MMR assumptions and sensitivity bands; describe as estimated exposure. | Observed-event comparison with known feed completeness limits before any predictive use; never label estimated volume as observed liquidation totals. |
| Confluence | Initially show real component values and disagreement; avoid converting handcrafted scores into probabilities. | Each input has provenance/freshness; later aggregation demonstrates incremental OOS benefit over the best component. |
| ML | Baseline logistic model and one tree family initially; temporal calibration; restricted tuning budget and out-of-sample ablation. | Predictive improvement, calibration and cost-aware evidence gates; registry may correctly remain quarantined. |
| Ensemble | Wire real inputs only after component evaluation. Start with simple weights fitted/selected on prior data; compare to strongest component. | Incremental held-out value and graceful abstention on missing/stale inputs; stacking only with valid temporal out-of-fold predictions. |
| Alerts | Separate observed-event alerts from validated predictive alerts; add provenance, availability, cooldown and deduplication. | Replay/restart sends each logical event at most once under the documented delivery policy. |

Do not develop tick archives, a second-order Markov model, a more complex clusterer or an ensemble solely to make every feature appear complete. A descriptive or retired module can be the correct final product decision.

## Milestone E — execution simulation and forward paper evidence

1. Reuse one strategy decision contract and one execution semantics across offline replay and forward paper. Keep vectorized evaluation for screening; use sequential event handling where positions, capital and TP/SL interact.
2. Signal at finalized bar t; earliest allowed execution after the signal is available. Model spread, fees, slippage, position limits and funding for the specified instrument.
3. With only 1h/4h/1d OHLC, intrabar TP/SL ordering cannot be recovered. Mark ambiguous bars and report conservative/bounded scenarios; do not fabricate tick order or restore subhour history without a separate scope decision.
4. Compute portfolio PnL on a regular clock with capital constraints, mark-to-market equity, exposure and turnover. Stress cost assumptions at baseline/1.5x/2x and delayed execution. Report uncertainty rather than a universal pass threshold.
5. Freeze model/config and log all predictions, abstentions and decisions before outcomes, append-only. Explicitly separate replay, mock and real-time paper records.
6. Reconcile forward decisions with a replay using the exact recorded available inputs. Differences must be attributable to documented data revisions or fill assumptions.

Acceptance: identical decisions for identical recorded inputs; PnL/cash/position reconciliation tests; no new entry when model gate or freshness gate fails. Forward evaluation duration depends on sample size, dependence and regime coverage; 30/60/90 days are review dates, not automatic proof of edge. Live trading is outside this plan's implementation scope.

## Promotion and stopping rules

- Engineering pass: data lineage, temporal correctness, numerical fixtures, operational checks and reproducibility complete.
- Descriptive pass: output accurately describes its estimator, uncertainty and limitations.
- Predictive candidate: predeclared OOS metric improves over the relevant baseline with adequate coverage and dependence-aware uncertainty; search multiplicity accounted for.
- Economic candidate: instrument-correct execution simulation supports acceptable net expectancy/risk under predeclared costs and stress assumptions.
- Forward evidence: a frozen candidate survives prospective observation; otherwise remain experimental/quarantined.
- No fixed accuracy such as 52%, fixed trade count or Sharpe threshold is universally sufficient. Gates must be agreed before evaluating the chosen benchmark and must not be loosened after failure.
- After the limited Analog v2 comparison, if no supported lift appears, retain Analog as an exploratory browser and stop tuning that family. Apply equivalent trial budgets to rules, ML and ensemble.

## Delivery order

A -> B -> C -> D -> E. Begin immutable forward data/decision logging as soon as A/B make it truthful; do not label pre-gate candidates as approved paper strategies.

Backend correctness, Python evaluator and UI evidence contracts can be implemented in parallel after their shared spec is frozen. Statistical outputs depend on the data/correctness gates and must be rerun when those versions change.

First implementation package: truthful ensemble state, shared costs/horizon/evidence spec, closed-bar/availability and SMC fixtures, evaluator alignment. Next package: Analog v2 as the first complete vertical slice. Roll other modules through the same gates only after that slice proves the evaluation workflow works, regardless of whether Analog itself has edge.

## Primary sources consulted

1. Binance Spot WebSocket streams: finalized candle flag and local depth synchronization. https://github.com/binance/binance-spot-api-docs/blob/master/web-socket-streams.md
2. scikit-learn common pitfalls: fit preprocessing/selection only on training data. https://scikit-learn.org/stable/common_pitfalls.html
3. scikit-learn TimeSeriesSplit: chronological splits and gap semantics. https://scikit-learn.org/stable/modules/generated/sklearn.model_selection.TimeSeriesSplit.html
4. scikit-learn probability calibration: separate calibration data; reliability diagrams; small-sample isotonic limitations. https://scikit-learn.org/stable/modules/calibration.html
5. Bailey and Lopez de Prado, The Deflated Sharpe Ratio. https://www.davidhbailey.com/dhbpapers/deflated-sharpe.pdf
6. Bailey et al., The Probability of Backtest Overfitting. https://www.davidhbailey.com/dhbpapers/backtest-prob.pdf
7. arch time-series bootstraps: stationary, circular and moving-block methods. https://bashtage.github.io/arch/bootstrap/timeseries-bootstraps.html
8. statsmodels HAC covariance: dependence-aware regression covariance. https://www.statsmodels.org/dev/generated/statsmodels.stats.sandwich_covariance.cov_hac.html
9. QuantConnect fill-model documentation: distinguish execution price/quantity, spread and slippage. https://www.quantconnect.com/docs/v2/writing-algorithms/reality-modeling/trade-fills/key-concepts
10. Stock Indicators ATR: convergence/warmup warnings. https://python.stockindicators.dev/indicators/Atr/
11. Binance USD-M market data: funding history and mark-price data. https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data

These sources justify methodology. They do not validate this project's features or demonstrate that a BTC trading edge exists.
