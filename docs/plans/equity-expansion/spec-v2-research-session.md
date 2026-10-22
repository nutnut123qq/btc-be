# EQX v2 — data correctness + research session (spec & contract)

Ngày chốt: 2026-10-18. Bổ sung cho `spec.md` (v1 giữ nguyên làm lịch sử). Coordinator sở hữu file này.

Định hướng: BE thu thập / lưu nguồn gốc / chuẩn hóa / tính / phục vụ dữ liệu. **Session Claude/Codex** (qua skill
`investment-research`) dùng công cụ + subagent để nghiên cứu, phản biện và ghi nhận định vào journal. Không phát triển lớp
LLM trong app; không gọi API LLM trả phí; không deploy.

Baseline: backend `209c85d`, ai `0be0858`, frontend `7f0e767` + diff EQX v1 chưa commit (giữ nguyên, xây tiếp).
Production (read-only check 2026-10-18): backend HEAD `2f047ff`, chưa có bảng equity/InstrumentCandles — không dùng DB test
làm bằng chứng production.

## A. Tính đúng của dữ liệu và quyết định

### A1 Calendar (single source of truth)
- Hiện trạng (preflight): BE dùng `MbbIngestion:NonTradingDates` (thiếu 2026-08-31, 2026-09-01); Python đọc env
  `MBB_NON_TRADING_DATES` mà **không process nào set** ⇒ Python không có ngày nghỉ nào.
- Canonical: file checked-in `backend/Data/Calendars/vn-hose-non-trading-days.json`:
  `{schemaVersion "vn-hose-calendar-v1", venue "HOSE", coverage {fromDate, toDate}, days[{date, kind
  market-holiday|market-closure, reason, sourceUrl, sourceTitle, verification verified|unverified-legacy}]}`.
  Chỉ ghi `verified` khi có thông báo chính thức (HOSE/SSC/UBCKNN, hoặc văn bản Chính phủ + xác nhận lịch giao dịch HOSE).
  Ngày cũ không tìm được nguồn ⇒ `unverified-legacy` (vẫn coi là không giao dịch để không tạo gap giả, nhưng data-quality
  báo rõ). Không thêm ngày chỉ vì thiếu nến.
- BE: `VietnamMarketCalendar` nạp từ file này (∪ override cấu hình nếu có, báo trong data-quality). BE đồng bộ idempotent
  file → bảng `MarketCalendarDays` lúc khởi động (`MarketCalendar:SyncOnStartup`, mặc định true). `GET /api/market-calendar/vn-hose`
  public trả danh sách + nguồn.
- Python: đọc `MarketCalendarDays` từ DB (batch scripts) — env `MBB_NON_TRADING_DATES` chỉ là override cho test/fixture.
  Bảng rỗng/không đọc được ⇒ **fail closed** (`calendar-unavailable`), không mặc định "mọi weekday là ngày giao dịch".
- Gap classification (data-quality): expected open không có bar ⇒ `market-wide-missing` (mọi mã scope + VNINDEX đều thiếu:
  nghi đóng cửa chưa xác minh) vs `symbol-missing` (mã khác có bar: provider thiếu hoặc mã bị đình chỉ — không tự gán lý do).
- Regression: continuity/warmup qua kỳ nghỉ hợp lệ không reset (BE indicator + Python conditions/base-rates), gap thật vẫn bị
  phát hiện. Sau sửa: recompute MBB/BID trên `btc_eqx_test`, rebuild conditions/replay/base-rates; chứng minh bằng dữ liệu thật
  (window length, warmup index, so sánh trước/sau) — không chỉ SMA50 ≠ null.

### A2 Journal constraints (migration mới `EquityResearchSessionLane`)
- `CK_MbbSignalObservations_FillCoherence` / `_OutcomeCoherence`: nhánh "set" thêm `IS NOT NULL` cho mọi cột so sánh
  (FillBarOpenTimeMs, FillObservedAtUtc, OutcomeObservedAtUtc…) — không còn NULL lọt.
- Trigger PostgreSQL write-once cho `MbbSignalObservations` và `EquityForecastRecords`: cột decision/forecast core bất biến sau
  insert; cột fill/outcome chỉ được đổi NULL → giá trị (không sửa/xoá khi đã set). Áp cho mọi writer (EF + raw SQL).
- Preflight script `backend/ops/preflight-equity-journal.sql`: liệt kê row vi phạm constraint/trigger mới **trước** khi migrate;
  migration không sửa/xoá dữ liệu để pass. Migration lịch sử giữ nguyên.

### A3 Tiền mặt + corporate actions
- Một quỹ tiền chung: bảng `EquityCashAccounts` (AccountId "default", AvailableCashVnd, UpdatedAtUtc). Per-symbol
  `EquityPositionAccounts.BuyBudgetVnd` (nullable) = ngân sách mua thêm khai báo rõ; cột cũ `AvailableCashVnd` per-symbol =
  legacy, không dùng cho sức mua (không tự gộp — migration để quỹ chung trống ⇒ "cash-unknown" tới khi user nhập).
- Ràng buộc: Σ BuyBudgetVnd (các mã) ≤ quỹ chung (kiểm khi ghi; khi quỹ giảm dưới tổng ⇒ decision báo `budget-exceeds-cash`,
  không đề xuất số lượng). Budget null ⇒ không đề xuất số lượng mua (reason `buy-budget-undeclared`).
- API: `PUT /api/equities/cash` (AdminGuard, `{availableCashVnd}`); `GET` nằm trong position DTO (additive: `sharedCashVnd`,
  `buyBudgetVnd`, `cashSource`). PUT position: `buyBudgetVnd` additive; `availableCashVnd` cũ trong body ⇒ ghi vào quỹ chung
  (một quỹ ⇒ không nhân đôi), response nêu rõ.
- Corporate action: `EquityCorporateActions.ShareRatio` (decimal nullable: cổ phiếu mới / 1 cổ phiếu cũ). Tự quy đổi lot có
  AcquireDate < ExDate chỉ khi `VerificationStatus=verified` ∧ loại ∈ {stock-dividend, bonus-shares, split} ∧ ShareRatio có:
  shares × (1+r), cost/share ÷ (1+r), với r = `ShareRatio` = số cổ phiếu **nhận thêm** trên 1 cổ phiếu cũ cho cả
  stock-dividend, bonus-shares và split (split 1:2 ⇒ r = 1.0, KHÔNG phải 2.0); cash-dividend: không đổi share/cost (ghi chú tiền cổ tức không
  tính vào PnL). rights-issue/other/unverified ⇒ phép tính PnL/proceeds/cost-based levels = null + reason
  `corporate-action-unconverted:{type}`. Proceeds dùng share đã quy đổi.

### A4 Relative strength
- Benchmark có bar mới nhất cũ hơn bar mới nhất của mã > 1 phiên giao dịch, hoặc aligned window kết thúc trước bar mới nhất
  của mã ⇒ benchmark `stale` + reason (`benchmark-stale` / `aligned-window-ends-before-instrument-latest`), không trả `ok`.

## B. LLM trong app ⇒ disabled; journal cho session

- `EquityLlm:Enabled` (BE, mặc định false) ⇒ `POST /llm-runs` trả 503 `LLM_DISABLED` trước mọi gọi upstream, không lưu run
  mới. AI: `EQUITY_LLM_ENABLED` (mặc định off) ⇒ `/api/equity-llm/run` 503 `LLM_DISABLED` trước khi tạo client. Lịch sử
  run/forecast `llm-judge` cũ vẫn đọc được. FE: bỏ nút tạo run, hiển thị "Lớp LLM trong app đã ngừng — nghiên cứu qua session".
  Dossier/context/quant/journal không phụ thuộc LLM.
- Journal session: bảng `EquityResearchNotes`:
  `NoteId` unique (`session/{SYMBOL}/{signalBarOpenTimeMs}/{shortid}`), Symbol ∈ {MBB,BID}, Mode `forward|retrospective`,
  RequestedAsOfUtc, SignalBarOpenTimeMs/CloseTimeMs, HorizonSessions, SnapshotId, SnapshotSha256, SkillVersion,
  MethodVersion, Direction `up|down|sideways|no-view`, ActionView `buy-more|hold|reduce|sell|wait|none`, ActionBasis
  `position-aware|market-only|insufficient-input`, BodyJson (jsonb `equity-research-note-v1`: thesis, evidence[{claim,
  evidenceIds[]}], counterEvidence[], scenarios[{name, horizonSessions, description, invalidation[]}], limitations[],
  missingData[], outcomeCheck{how, dueAfterSessions}, probability?{configId, probTargetFirst, probStopFirst, method,
  methodRef}), AvailableTimeMs (thời điểm ghi thật), CreatedAtUtc. Bất biến sau insert (trigger).
- Ghi: `POST /api/equities/{symbol}/research-notes` (AdminGuard). `forward` chỉ khi SignalBarOpenTimeMs = bar 1d finalized
  mới nhất và now < open phiên kế (như rule LLM). `retrospective` cho as-of quá khứ: lưu được, **không bao giờ** sinh
  forecast record/được chấm như forward. Probability chỉ chấp nhận khi `method` ≠ rỗng và nêu `methodRef` (tài liệu/script
  tái lập); khi có ⇒ thêm `EquityForecastRecords` Source `session-research` (Validated=false, ResearchNoteId) để chấm như
  quant/llm. Đọc: `GET /api/equities/{symbol}/research-notes` (AdminGuard vì ActionView có thể suy ra vị thế).
- Không có thao tác đặt lệnh thật ở bất kỳ đâu.

## C. Nguồn gốc dữ liệu, snapshot, skill

### C1 Source documents
- Bảng `EquitySourceDocuments`: DocumentId (guid), Symbol, Entity (tên pháp nhân), DocType `financial-statement|
  investor-presentation|disclosure|annual-report|other`, Title, SourceUrl, Provider (vd `issuer:BIDV`), ContentSha256,
  StoragePath (tương đối so với `EquityDocuments:RootPath`), ByteSize, MimeType, ReportingPeriod ("YYYYQn"/"YYYY"/null),
  Consolidation `consolidated|separate|unknown`, AuditStatus `audited|reviewed|unaudited|unknown`, PublishedAt?,
  FirstSeenAtUtc, FetchedAtUtc, Revision, SupersedesDocumentId?, ExtractionJson? (jsonb provenance của trích xuất).
  Unique (Symbol, ContentSha256). Bản gốc lưu ngoài PG dưới `EquityDocuments:RootPath` (mặc định workspace
  `D:\code\btc\data\equity-documents\{SYMBOL}\{sha256}.{ext}`, cấu hình được; không commit file).
- `EquityFundamentals` thêm `DocumentId` (nullable FK) + `SourceLocation` (trang/bảng/dòng). Metric từ tài liệu gốc dùng
  Source `issuer-report` — không ghép với KBS/VCI theo tên metric.
- Admin: `POST /api/equities/{symbol}/admin/source-documents` (đăng ký metadata đã tải, validate hash/path tồn tại).
  Public: `GET /api/equities/{symbol}/source-documents` (metadata, không serve file).
- Collector Python bounded: coverage mục tiêu = BCTC hợp nhất 4 quý gần nhất + BCTC năm gần nhất cho BID và MBB từ trang
  nhà đầu tư chính thức; request budget, dedup theo hash, resume. Thiếu trích xuất ⇒ công bố thiếu.

### C2 Readiness + snapshot
- BE: `GET /api/equities/{symbol}/research-readiness?asOf=` và `GET /api/equities/{symbol}/research-snapshot?asOf=`
  (public, market-only, không có holdings), dựng trong 1 transaction `RepeatableRead`.
- Readiness `equity-research-readiness-v1`: per purpose `technical|fundamental|quantitative|portfolio-action` ⇒
  `{status ready|partial|not-ready, reasons[], checks[]}`; không có cờ ready chung. portfolio-action luôn cần quyền đọc vị thế
  ⇒ public trả `requires-admin` cho phần đó.
- Snapshot `equity-research-snapshot-v1`: snapshotId (`snap/{SYMBOL}/{cutoffMs}/{sha12}` — sha của nội dung canonical trừ
  generatedAt), symbol, requestedAsOf, effectiveCutoffMs, generatedAt, methodVersion, dataset{per timeframe fingerprint},
  coverage/freshness/gaps, priceBasis + limitations (latest-adjusted ⇒ `pit-reconstruction: partial|not-possible`),
  conditions (chỉ khi as-of = latest; quá khứ ⇒ unavailable `conditions-not-reconstructible-for-past-asof` trừ khi tái tính
  được), series{timeframe, endpoint, endTimeMs, barCount, fingerprint} (để lấy chuỗi đầy đủ qua CLI), fundamentals/
  corporateActions/events lọc `AvailableAtUtc ≤ asOf`, relativeStrength (≤ asOf), baseRates (artifact chỉ khi
  dataAsOf ≤ asOf; khác latest ⇒ abstained `artifact-not-pit-for-asof`), sourceDocuments refs, unavailable[{section,
  reason}], howToGoDeeper[].
- CLI Python `ai/research_snapshot.py` (gọi BE API, không đọc trực tiếp toàn DB): `readiness`, `export --out-dir`
  (snapshot.json + series CSV phân trang ≤ cutoff + manifest sha256; kiểm fingerprint khớp, lệch ⇒ fail), `export
  --include-portfolio` (cần `EQX_ADMIN_KEY`, ghi file riêng `portfolio.json`), `journal record --file` (POST research-notes).

### C3 Skill `investment-research`
- Vị trí version-controlled: `ai/skills/investment-research/` (SKILL.md ngắn + references/ + dùng `ai/research_snapshot.py`,
  không nhân bản code). Gọi bằng đường dẫn tuyệt đối trong prompt session mới. Không sửa global skills/config.

## D. Gates / handoff
- Như `spec.md` §10 + Playwright live; reviewer read-only; forward-test skill bằng subagent context mới.
- Handoff: `backend/docs/plans/equity-expansion/deploy-handoff.md` (diff/commit theo repo, thứ tự migration + backup +
  preflight, bounded backfill/recompute, config bật, health/coverage checks, rollback + giới hạn schema). Không thực thi production.
