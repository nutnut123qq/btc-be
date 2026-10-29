# Equity expansion (EQX) — BID/MBB lane: spec & contract

Ngày chốt: 2026-10-16. Coordinator sở hữu file này; worker cần đổi contract phải báo coordinator.
Baseline: backend `482948e` + dirty MBB FIX round (AdminGuard đọc position/decision, fill ≥ AvailableTimeMs,
trigger direction — migration `20261016065010_TightenMbbObservationFillCoherence`), ai `0be0858`,
frontend `5f9e5c8` + dirty FE companion (locked-state khi chưa có admin key). Dirty baseline giữ nguyên.

## 1. Instrument scope

| Symbol | AssetClass | Quote unit (`Currency`) | PriceBasis | Timeframes | Ingestion | TechnicalAnalysis | Decision (position/decision/observe/base-rates/context/dossier/LLM) |
|---|---|---|---|---|---|---|---|
| BTCUSDT | crypto | USDT | raw | 1h,4h,1d | (Binance lane, không đổi) | ✓ | — (lane BTC riêng) |
| MBB | equity | VND | adjusted | 1h,1d | ✓ | ✓ | ✓ |
| BID | equity | VND | adjusted | 1h,1d (1h từ EQTECH-1; journal vẫn chỉ 1d) | ✓ | ✓ | ✓ |
| VCB | equity | VND | adjusted | 1d | ✓ | — | — (context-only: peer RS) |
| CTG | equity | VND | adjusted | 1d | ✓ | — | — (context-only: peer RS) |
| VNINDEX | **index** | **index-points** | raw | 1d | ✓ | — | — (context-only: benchmark RS) |

- `InstrumentCapabilities` thêm 2 flag additive: `Ingestion`, `Decision`. Crypto flags giữ false cho mọi equity/index.
- `InstrumentAssetClasses.Index = "index"`. VNINDEX không mang lot/fee/tax/band VND; mọi code VND-cost chỉ chạy cho `Decision=true`.
- `Enabled` giữ nguyên nghĩa "enabled cho production traffic của endpoint BTC/crypto" → **mọi equity/index Enabled=false**.
  Crypto endpoints tiếp tục từ chối equity qua `ProductionSymbolScopeFilter` (400 `UNSUPPORTED_SYMBOL`).
- Catalog contract version: `instrument-catalog-v2` (additive).
- Python `trading_config.INSTRUMENTS` mirror cùng semantics (`capabilities.ingestion`, `capabilities.decision`,
  `asset_class` "index"). `ACTIVE_SYMBOLS` **giữ** `["BTCUSDT"]` — BID không vào job crypto.
- Probe W0 (`evidence/equity-expansion/w0-provider-probe.md`, live 2026-10-16) xác minh: VCI gap-chart nhận BID/VCB/CTG/VNINDEX
  nguyên dạng, 1 symbol/request; daily ts = 00:00 UTC (07:00 VN) như MBB; giá cổ phiếu VND đầy đủ, VNINDEX điểm (2 số lẻ),
  volume cổ phiếu; bar đang hình thành được trả trong phiên (giữ finalize guard). VNINDEX có ~29 row lỗi quanh 2005 ⇒
  backfill VNINDEX từ 2010-01-01 + validator OHLC; không cross-check volume VNINDEX. KBS index dùng `/index/VNINDEX/data_day`.
- **Price basis:** cả VCI và KBS back-adjust TOÀN BỘ lịch sử ở mỗi ngày GDKHQ (cổ tức tiền lẫn cổ phiếu). Hệ quả bắt buộc
  cho ingestion: khi sync incremental phát hiện bar finalized đã lưu bị đổi giá (revision) ⇒ refetch toàn lịch sử symbol/
  timeframe đó (bounded pages) để không trộn hai hệ số điều chỉnh trong DB. Stored levels không phải as-of-time.
- Không tạo nến 4h tổng hợp. BID không có 1h ⇒ không bao giờ stale vì thiếu 1h.

## 2. API surface

Route mới: `/api/equities/...`, controller đánh dấu `[SkipProductionSymbolScope]` (filter bỏ qua) và tự resolve
symbol qua `EquityScope.Resolve(symbol, capability)`:

| Lỗi | HTTP | Code |
|---|---|---|
| Symbol không có trong catalog | 404 | `UNKNOWN_SYMBOL` |
| Known nhưng thiếu capability (BTCUSDT trên /equities; VCB trên /decision; …) | 400 | `UNSUPPORTED_CAPABILITY` (message nêu capability) |
| Timeframe ngoài `ActiveTimeframes` của symbol | 400 | `INVALID_TIMEFRAME` |
| Thiếu/sai admin key | 401 | (AdminGuard envelope hiện có) |
| Lane đang chạy sync/recompute/observe/LLM cùng symbol | 409 | `LANE_BUSY` (retryable=true) |
| Upstream AI/LLM không khả dụng | 502/503 | `CONDITIONS_SOURCE_UNAVAILABLE` / `LLM_UNAVAILABLE` |

Mọi lỗi dùng `ApiErrorEnvelope`.

| Route | Method | Capability | Auth | Ghi chú |
|---|---|---|---|---|
| `/api/equities` | GET | — | public | danh sách symbol equity/index + capabilities + ingestion status per symbol |
| `/api/equities/{symbol}/candles?timeframe&limit&endTimeMs` | GET | Ingestion | public | shape `MbbCandleDto` |
| `/api/equities/{symbol}/data-quality` | GET | Ingestion | public | shape `MbbCoverageReport` |
| `/api/equities/{symbol}/conditions?timeframe` | GET | TechnicalAnalysis | public | proxy AI, cache key symbol:tf |
| `/api/equities/{symbol}/observations?timeframe&take` | GET | Decision | public | shape `MbbObservationDto` (+`symbol`) |
| `/api/equities/{symbol}/position` | GET/PUT | Decision | **AdminGuard** | shape `MbbPositionDto` / `UpdateMbbPositionRequest` |
| `/api/equities/{symbol}/decision` | GET | Decision | **AdminGuard** | shape `MbbDecisionDto` |
| `/api/equities/{symbol}/base-rates` | GET | Decision | public | §5 |
| `/api/equities/{symbol}/context` | GET | Decision | public | §6 |
| `/api/equities/{symbol}/dossier` | GET | Decision | public | §7 — market-only, KHÔNG chứa position |
| `/api/equities/{symbol}/llm-runs?take` | GET | Decision | public | danh sách run (market-only) |
| `/api/equities/{symbol}/llm-runs` | POST | Decision | **AdminGuard** + rate-limit `expensive` | tạo 1 run LLM (đắt) |
| `/api/equities/{symbol}/forecast-journal?configId&take` | GET | Decision | public | §8 forecasts + scoring |
| `/api/equities/{symbol}/admin/sync` · `/backfill` · `/recompute` · `/observe` | POST | Ingestion / TA / Decision | **AdminGuard** | tương đương /api/mbb/admin/* |
| `/api/equities/{symbol}/admin/corporate-actions` · `/events` · `/fundamentals` | POST | Decision ∪ context symbols | **AdminGuard** | batch upsert có validation (§6) |
| `/api/equities/{symbol}/admin/base-rates/rebuild` | POST | Decision | **AdminGuard** | gọi AI build artifact |

`/api/mbb/*` giữ nguyên route + shape, là alias cố định `symbol=MBB` gọi cùng service. FE mới: MBB tiếp tục dùng
`/api/mbb/*` cho surface cũ (an toàn khi BE cũ); surface mới (base-rates/context/dossier/journal/llm) và BID dùng
`/api/equities/{symbol}`. 404 trên surface mới ⇒ FE hiển thị unavailable, không crash (pitfall #30).

Field additive trong DTO cũ: `symbol` trên observation DTO; position/decision đã có `symbol`.

## 3. Persistence

Coordinator viết entity (`Data/EquityLaneRecords.cs`) + `AppDbContext` + migration `20261016072541_EquityExpansionLane`
(đã test trên `btc_eqx_test`: `evidence/equity-expansion/eqx-00-migration-test.log`), worker không sửa.

- `CK_MbbSignalObservations_Scope` → `("Symbol"='MBB' AND "Timeframe" IN ('1h','1d')) OR ("Symbol"='BID' AND "Timeframe"='1d')`.
  Vẫn đúng sau EQTECH-1: BID có nến 1h nhưng journal không ghi BID 1h (guard `MbbSignalObservationService.JournalTimeframes`).
  Migration lịch sử giữ nguyên. Các CHECK chronology/fill/outcome/clock giữ nguyên.
- DecisionId mới: `equity-observe/{SYMBOL}/{tf}/{signalBarOpenTimeMs}`, `RecorderVersion=equity-observe-v2`. Row MBB cũ
  (`mbb-observe/...`, v1) giữ nguyên, không rewrite. Unique (Symbol,Timeframe,SignalBarOpenTimeMs) chặn ghi trùng giữa 2 format.
- Bảng mới (đặt tên `Equity*`; nợ kỹ thuật: bảng observation vẫn tên `MbbSignalObservations`):
  - `EquityCorporateActions` (Symbol, ExDate, ActionType ∈ cash-dividend|stock-dividend|bonus-shares|rights-issue|split|other,
    RatioText, CashPerShareVnd?, AdjustmentFactor?, RecordDate?, AnnouncedDate?, SourceUrl (bắt buộc http/https), Provenance
    ∈ provider:VCI|provider:KBS|admin-manual, VerificationStatus ∈ unverified|verified|rejected, AvailableAtUtc, Created/UpdatedAtUtc;
    unique (Symbol, ExDate, ActionType)).
  - `EquityEvents` (Symbol, EventType ∈ earnings|agm|additional-listing|other, EventDate, Title, SourceUrl, Provenance,
    AvailableAtUtc, …; unique (Symbol, EventType, EventDate, Title)).
  - `EquityFundamentals` (Symbol, Period "YYYYQn", PeriodEndDate, Metric ∈ BVPS|PB|ROE|NIM|NPL|COVERAGE|LLR|CAR|LDR, Value?,
    Unit ∈ VND|ratio|percent, UnavailableReason? (bắt buộc khi Value null), PublishedDate?, AvailableAtUtc, AsOfUtc, Source,
    SourceUrl, Provenance; unique (Symbol, Period, Metric, Source)).
  - `EquityForecastRecords` (§8) và `EquityLlmRuns` (§7).

## 4. Worker `EquityIngestionWorker` (P0e)

Options `EquityIngestion` (mặc định **Enabled=false**, không bật trên production trong đợt này):
`Symbols` = [MBB, BID, VCB, CTG, VNINDEX] ∩ catalog `Ingestion=true`; `RunAfterLocal` "15:15"; `PollMinutes` 15;
`MaxAttemptsPerSession` 6; `BaseRatesRebuild` true; `RecordForecasts` true.

- Mỗi tick: nếu phiên VN hôm nay là ngày giao dịch và now ≥ 15:15 local và nến 1d phiên hôm nay chưa lưu (hoặc startup
  với phiên finalized gần nhất chưa có) → chạy **tuần tự từng symbol**: sync (timeframes của symbol, bounded như MBB) →
  nếu TA: recompute khi có bar mới → nếu Decision: observe (mọi timeframe của symbol) → base-rates rebuild (AI) → ghi quant forecast.
- Thất bại một symbol: log + status per-symbol `failed` với lý do, attempt++ ; symbol khác vẫn chạy. Retry ở tick sau tới
  `MaxAttemptsPerSession`, rồi `gave-up` cho phiên đó (không backoff vô hạn).
- Lock per-symbol `EquityLaneLocks` (static, dùng chung với admin controllers): đang giữ ⇒ admin trả 409 `LANE_BUSY`, worker skip symbol tick đó.
- Observe chỉ khi bar finalized mới nhất đã lưu (logic hiện có) — không backfill observation bị lỡ.
- Heartbeat qua `WorkerHeartbeatStore` = process sống; trạng thái dữ liệu per-symbol (`up-to-date|pending-provider|failed|gave-up|disabled`)
  là data-plane, không làm watchdog restart (pitfall #40).

## 5. Quant base-rates (P1) — protocol `equity-base-rates-protocol-v1` (đóng băng 2026-10-16, trước khi xem kết quả)

- Dữ liệu: `InstrumentCandles` 1d của đúng symbol, basis adjusted (VCI). Không pool BID/MBB, không dùng mã khác làm feature.
- Signal tại close bar t; khả dụng sau close + publish grace ⇒ **entry = open phiên t+1** (simulated, không phải lệnh khớp).
- Barrier configs (giả thuyết khai báo trước): `tb-p5-s4-h20` (+5%/−4%, 20 phiên), `tb-p10-s6-h60` (+10%/−6%, 60 phiên).
  Barrier tính từ entry price. Duyệt bar entry..entry+H−1:
  - open ≤ stop ⇒ stop-first, exit tại open (gap); open ≥ target ⇒ target-first, exit tại target (bảo thủ).
  - cùng bar high ≥ target và low ≤ stop ⇒ **stop-first** (bảo thủ, cờ `doubleTouch`).
  - hết H phiên ⇒ `timeout`, exit tại close bar cuối. Chưa đủ H bar ⇒ `immature` (loại khỏi train/eval, pending).
  - Thời điểm label biết được = close của bar resolve.
- Chi phí: `vn_execution_model` (phí mua 0.15%, phí bán 0.15%, thuế bán 0.1%). EV net = Σ p_class × return_net_class.
- Features (chỉ OHLCV adjusted của symbol tại t): ret5, ret20, ret60, vol20 (std log-ret), dist SMA50, dist SMA200, RSI14,
  ATR14%, volume z20. Warm-up 200 bar.
- kNN Euclidean trên features chuẩn hóa; scaler fit trên training-eligible set của từng điểm dự báo; ứng viên neighbor
  chỉ gồm mẫu có label **resolve trước** t (purge theo label availability). k ∈ {20,50,100} chọn bằng inner time-split
  (80/20) **trong** phần train của fold.
- Walk-forward expanding: OOS bắt đầu khi đủ 500 mẫu matured; fold theo năm lịch; mỗi fold refit scaler/k.
- Baseline: expanding prior (tần suất class trên mẫu matured trước t), cùng timestamps.
- Metrics OOS: Brier binary (target-first) kNN vs prior, reliability 5 bins + ECE, coverage/abstention, n, n_effective =
  số mẫu không chồng lấn (lấy mỗi H phiên), CI 90% của ΔBrier bằng **block bootstrap block=H** (1000 lần, seed cố định),
  EV net trung bình OOS (unconditional và khi EV dự báo > 0).
- **Gate** (cho phép `decisionUse`): n_effective ≥ 30 **và** CI90(ΔBrier = kNN − prior) upper < 0 **và** ECE ≤ 0.05.
  Fail ⇒ `validated=false`; số vẫn hiển thị trong `research` với nhãn unvalidated.
- Artifact `{EvidenceCatalog:RootPath}/equity-base-rates/{SYMBOL}/base-rates.json`, ghi atomic (tmp + replace), schema
  `equity-base-rates-artifact-v1`: symbol, timeframe, generatedAtUtc, dataAsOfOpenTimeMs, priceBasis, provider,
  dataset {barCount, firstOpenTimeMs, lastOpenTimeMs, maxUpdatedAtUtc, maxRevision, sha256}, method {name, version
  `equity-knn-v1`, features, kGrid}, protocol {version, frozenAt, entry, doubleTouch, gap, costs, gate}, configs[] {configId,
  targetPct, stopPct, horizonSessions, validation{gatePassed, gateReasons, nOos, nEffective, brierKnn, brierPrior,
  brierDiffCi90, ece, calibrationBins, coverage, abstentionRate, evNetOosMean, evNetOosWhenPositive}, current{signalBarOpenTimeMs,
  probTargetFirst, probStopFirst, probTimeout, evNetFraction, k, nEligible, prior{probTargetFirst, probStopFirst, probTimeout}}}.
  Report OOS tái lập: `evidence/equity-expansion/base-rates/{SYMBOL}-report.json` (cùng nội dung + per-fold).
- AI: `ai/equity_base_rates.py` (CLI `--symbol`, `--candles-json` fixture, `--out`), FastAPI `POST /api/equity-base-rates/build?symbol=`.
- BE `GET /base-rates` response `equity-base-rates-v1`: `{contractVersion, symbol, timeframe, status ok|abstained|unavailable,
  reasons[], artifact{generatedAtUtc, dataAsOfOpenTimeMs, priceBasis, methodVersion, protocolVersion, datasetSha256}|null,
  configs[]{configId, targetPct, stopPct, horizonSessions, abstained, abstainReasons[], decisionUse{probTargetFirst,
  probStopFirst, probTimeout, evNetFraction}|null, research{label validated|unvalidated, …current…}|null, validation{…}}}`.
  Kiểm tra: schemaVersion đúng, symbol/timeframe/priceBasis khớp catalog, fingerprint (count/first/last/maxUpdatedAt trên bar
  ≤ lastOpenTimeMs) khớp DB (else `artifact-stale-revision`), `dataAsOfOpenTimeMs` == bar 1d finalized mới nhất đã lưu
  (else `artifact-stale-asof`). Thiếu artifact ⇒ `unavailable` + `artifact-missing`, **không fallback mã khác**.
  `decisionUse` chỉ non-null khi gatePassed ∧ fresh ∧ fingerprint khớp.

## 6. Context (P2) — `equity-context-v1`

`{contractVersion, symbol, generatedAtUtc, relativeStrength, corporateActions, events, fundamentals, limitations[]}`; mỗi phần
`{status: ok|empty|unavailable, reason?, …}`.
- RS: từ `InstrumentCandles` 1d đã ingest; align theo **ngày phiên chung** (intersection), windows 20/60 phiên;
  benchmarks: VNINDEX (index) + peers (VCB, CTG, và mã còn lại trong BID/MBB). Mỗi benchmark: status, alignedSessions,
  returns{window: {instrument, benchmark, excess}}. Thiếu benchmark/không đủ phiên ⇒ benchmark đó unavailable có lý do.
- Corporate actions / events / fundamentals: từ bảng §3; metric có Period, PublishedDate, AvailableAtUtc, AsOfUtc, Source,
  SourceUrl, Unit. Null ⇒ `unavailableReason`. Không tự suy.
- Nguồn (chốt theo probe W0), collector ở **BE** (`EquityContextCollector`, dùng chung validation với admin import):
  - Corporate actions + events: VCI `GET iq.vietcap.com.vn/api/iq-insight-service/v1/events` (no auth). Publish date chỉ có
    ngày ⇒ `AvailableAtUtc` = 00:00 VN của ngày **sau** publish date (bảo thủ). Provenance `provider:VCI`, `unverified`.
    VNINDEX không có events.
  - Fundamentals: **chỉ KBS** `finance-info/{sym}?type=CSTC&termtype=2` (có report date + publication date): BVPS, PB, ROE,
    NIM (theo quý, KHÔNG annualize — ghi note), LDR, LLR. `AvailableAtUtc` = 00:00 VN ngày sau publication date.
  - NPL, COVERAGE, CAR: `unavailable` với lý do "no point-in-time source verified" (VCI statistics-financial thiếu ngày công
    bố và gắn sai kỳ — 2026-Q3 chứa số Q2). Admin có thể import có SourceUrl.
  - Mỗi field lấy từ đúng 1 nguồn (định nghĩa VCI/KBS khác nhau, ví dụ LDR).
  - Admin import `/admin/*` (validation: SourceUrl http(s), enum, ngày không ở tương lai, Value null ⇔ UnavailableReason).
- Không mặc định công thức adjustment: `AdjustmentFactor` null trừ khi nguồn cung cấp/verified.
- Decision: nếu có corporate action (verified hoặc unverified) ex-date > ngày mua lot sớm nhất ⇒ decision thêm reason
  `corporate-action-since-acquisition` và chặn `sell`/`reduce` theo stop-loss (adjusted vs giá vốn thật chưa quy đổi) ⇒ `wait`.
- Source sửa lịch sử ⇒ candles Revision/UpdatedAt đổi ⇒ base-rates `artifact-stale-revision` (§5).

## 7. Dossier + LLM (P3)

- `GET /dossier` = `{contractVersion equity-dossier-v1, symbol, asOfUtc, signalBarOpenTimeMs, conditions(1d)|unavailable,
  baseRates, context, latestLlmRun|null, journalSummary}`. Không chứa position/cost/cash.
- `POST /llm-runs` (AdminGuard, expensive limiter, lane lock): BE dựng dossier → gọi AI `POST /api/equity-llm/run` (timeout
  config) → validate output → lưu `EquityLlmRuns` (+ `EquityForecastRecords` source `llm-judge` nếu có adjustedForecasts).
  Chỉ cho phép khi `signalBarOpenTimeMs` của dossier = bar 1d finalized mới nhất (không backdate).
- AI `ai/equity_llm.py`: 2 call Claude Messages API qua SDK chính thức `anthropic` (thêm vào requirements.txt — skill claude-api
  yêu cầu SDK thay vì raw HTTP), model mặc định `claude-opus-5-5`, `thinking: adaptive`, effort đặt tường minh, server-side refusal
  fallback (`fallbacks: "default"` + beta `server-side-fallback-2026-07-01`), kiểm tra `stop_reason` (refusal/max_tokens/pause_turn).
  Researcher có server tool `web_search_20260209` (`max_uses` = `EQUITY_LLM_MAX_SEARCHES`), trả text + nguồn từ
  `web_search_tool_result`; judge KHÔNG tool, dùng `output_config.format` (json_schema; không dùng citations cùng lúc);
  judge không tool, output JSON schema `equity-llm-judge-v1`: `{schemaVersion, symbol, signalBarOpenTimeMs, evidenceStatus
  sourced|insufficient, scenarios[{name, direction up|down|sideways, description, sourceIds[]}], warnings[{text, sourceIds[]}],
  veto{active, reason, sourceIds[]}, sources[{id, url, title, publishedAt|null}], adjustedForecasts[{configId,
  probTargetFirst, probStopFirst, rationale, sourceIds[]}]}`. Validate: sourceIds tồn tại, configIds ∈ configs §5,
  0≤p, pT+pS≤1, không field giá/mốc. Dữ liệu web/dossier là untrusted input (bọc delimiter, chỉ dẫn hệ thống không lấy từ nó).
- Config env AI: `ANTHROPIC_API_KEY`, `EQUITY_LLM_RESEARCH_MODEL`, `EQUITY_LLM_JUDGE_MODEL`, `EQUITY_LLM_MAX_TOKENS`,
  `EQUITY_LLM_TIMEOUT_S`, `EQUITY_LLM_MAX_SEARCHES`. Thiếu key ⇒ AI 503 `LLM_UNAVAILABLE` ⇒ BE lưu run status `unavailable`
  + trả 503. Key không log/commit.
- LLM chỉ để hiển thị + journal; **không** đổi action của decision, không vượt hard gates; veto chỉ thành cảnh báo.
- `EquityLlmRuns`: RunId, Symbol, SignalBarOpenTimeMs, CreatedAtUtc, Status succeeded|failed|unavailable, Reason,
  ResearchModel, JudgeModel, PromptVersion, SchemaVersion, InputSnapshotSha256, InputSnapshotJson, OutputJson, UsageJson, DurationMs.

## 8. Forecast journal

`EquityForecastRecords`: ForecastId unique `{source}/{SYMBOL}/{configId}/{signalBarOpenTimeMs}[/{runId}]`, Source
`quant-knn|llm-judge`, Symbol, Timeframe `1d`, ConfigId, TargetPct, StopPct, HorizonSessions, SignalBarOpenTimeMs,
SignalBarCloseTimeMs, AvailableTimeMs, CreatedAtUtc, ProbTargetFirst?, ProbStopFirst?, Abstained, AbstainReason?,
Validated (llm luôn false), MethodVersion, ModelId?, PromptVersion?, SchemaVersion?, InputSnapshotSha256, LlmRunId?;
outcome write-once: EntryBarOpenTimeMs, EntryPrice, OutcomeLabel target-first|stop-first|timeout, OutcomeReturnNet,
ResolvedBarOpenTimeMs, OutcomeResolvedAtUtc.
- CHECK: outcome all-null hoặc all-set; EntryBarOpenTimeMs > SignalBarOpenTimeMs; AvailableTimeMs ≥ SignalBarCloseTimeMs;
  prob ∈ [0,1], pT+pS ≤ 1; Abstained ⇒ prob null; Source llm-judge ⇒ Validated=false.
- Ghi chỉ cho bar finalized mới nhất (như observation). Quant ghi research prob khi artifact fresh (Validated = gatePassed).
- Outcome resolver (worker + admin observe): entry = bar 1d đầu tiên open ≥ AvailableTimeMs; áp đúng rule barrier §5 trên
  bar đã finalized; chưa đủ H phiên ⇒ pending (null).
- Scoring (`forecast-journal`): paired = cùng symbol/config/signalBar, quant không abstained, llm có prob, outcome resolved.
  Báo pairedN, nEffective (không chồng lấn theo H), brierQuant, brierLlm, diff, coverage mỗi source; nEffective < 30 ⇒
  `status=insufficient-sample`, không kết luận. Không auto-promote LLM.

## 9. Frontend (P4)

- `EquityScreen` (tổng quát từ `MbbScreen`), route `/co-phieu?symbol=BID|MBB` (URL là source of truth; default MBB);
  `/mbb` render cùng screen symbol MBB (đổi mã ⇒ push `/co-phieu?symbol=X`).
- Panels: chart + conditions (MBB 1h/1d; BID 1d lúc viết spec — backend có BID 1h từ EQTECH-1, FE đã dừng phát triển), decision + position (AdminGuard, locked state), data quality,
  base-rates (abstained/unvalidated/stale rõ ràng), context, journal/scoring, LLM runs (admin nút tạo run).
- States: loading/empty/error/stale/abstained/unavailable/permission. Request-id guard per symbol (pitfall #29); không gọi
  Binance websocket/crypto endpoints trên trang equity. Field additive degrade (pitfall #30).

## 10. Gates & DoD chung

- Backend (cwd backend): `dotnet build Backend.csproj -c Release`; `dotnet test Tests/Backend.Tests/Backend.Tests.csproj -c Release`;
  PG integration với `BTC_TEST_POSTGRES` trỏ DB test `btc_eqx_test` (KHÔNG phải `bitcoin_analyst`); contract `ops/contract.ps1`.
- AI (cwd ai): `venv/Scripts/python.exe -m pytest -q`.
- FE (cwd frontend): xem frontend/package.json; binary node trực tiếp (pitfall #23); contract copy BE→FE (pitfall #42).
- Không commit/push/deploy, không apply migration/bật worker trên production, không mua data.
- `git diff --check` sạch; file mới LF; ghi file UTF-8.

## 11. Contract pins (2026-10-16, sau Wave 1)

- Mọi tỷ lệ là **fraction**: `targetPct` 0.05, `stopPct` 0.04 (dương), probabilities, `evNetFraction`, RS returns,
  `OutcomeReturnNet`, `brierDiff*`. UI tự nhân 100 khi hiển thị.
- `brierDiffCi90` = mảng `[lower, upper]` hoặc `null`.
- Context: `corporateActions.items[]`, `events.items[]`, `fundamentals.items[]` (mỗi item = 1 metric row: `period`,
  `periodEndDate`, `metric`, `value|null`, `unit`, `unavailableReason?`, `publishedDate|null`, `availableAtUtc`, `asOfUtc`,
  `source`, `sourceUrl`, `note?`); `relativeStrength.benchmarks[]` = `{symbol, kind index|peer, status, reason?,
  alignedSessions, returns: {"20": {instrument, benchmark, excess}, "60": {...}}}`.
- Section `ok` với 0 row được coi như `empty`. FE từ chối `decisionUse` khi gate không pass / config abstained.
- `maxUpdatedAtUtc` trong fingerprint: `YYYY-MM-DDTHH:MM:SS.ffffffZ` (micro giây, như Postgres); BE so sánh đúng precision này.
- Quant limitations đã khai báo (không đổi protocol): eligibility purge làm lệch nhẹ neighbor gần (timeout chưa biết trong H
  bar cuối); gap-through-stop phổ biến ⇒ lỗ thực > stop danh nghĩa; giá adjusted không point-in-time, adjustment volume chưa
  xác minh (ảnh hưởng `volumeZ20`).
- `GET /forecast-journal` → `{contractVersion "equity-forecast-journal-v1", symbol, items[{forecastId, source, configId, targetPct,
  stopPct, horizonSessions, signalBarOpenTimeMs, availableTimeMs, createdAtUtc, probTargetFirst|null, probStopFirst|null,
  abstained, abstainReason|null, validated, methodVersion, modelId|null, promptVersion|null, llmRunId|null,
  outcome{entryBarOpenTimeMs, entryPrice, label, returnNet, resolvedBarOpenTimeMs, resolvedAtUtc}|null}],
  scoring[{configId, status ok|insufficient-sample|no-data, pairedN, nEffective, brierQuant|null, brierLlm|null,
  brierDiff|null, coverageQuant, coverageLlm}]}`.
- `GET /llm-runs` → `{items[{runId, symbol, signalBarOpenTimeMs, createdAtUtc, status, reason|null, researchModel|null,
  judgeModel|null, promptVersion, schemaVersion, output (equity-llm-judge-v1)|null, durationMs}]}`. `POST /llm-runs` trả
  1 item cùng shape (201 succeeded/failed; 503 `LLM_UNAVAILABLE` kèm item status unavailable; 409 `LANE_BUSY`;
  409 `SIGNAL_BAR_NOT_LATEST` khi dossier không ở bar finalized mới nhất).
- Fundamentals giữ giá trị **như nguồn công bố**: `unit=percent` ⇒ số phần trăm (ROE 18.2 nghĩa 18.2%; ROE/NIM/NPL/
  COVERAGE/LLR/CAR/LDR), `unit=ratio` ⇒ bội số (PB 1.40×), `unit=VND` (BVPS). (Khác quy tắc fraction của API quant ở trên.)
- `forecast-journal?configId` tùy chọn (thiếu ⇒ mọi config). `POST /llm-runs` trả 1 item cùng shape list.
- `brierDiff = brierLlm − brierQuant` (âm ⇒ LLM tốt hơn). Route bổ sung `POST /admin/forecasts/record` (AdminGuard) ghi quant
  forecast cho bar mới nhất, idempotent. Rule corporate-action chỉ tính ex-date ≤ hôm nay, bỏ qua `rejected`.
- KBS fundamentals: mỗi sync chỉ lấy quý mới nhất (header KBS lặp kỳ ⇒ map quý cũ chưa xác minh); lịch sử tích lũy dần.
