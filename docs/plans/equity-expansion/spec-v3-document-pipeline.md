# EQX v3 — issuer document → financial facts pipeline (spec & contract)

Ngày chốt: 2026-10-19. Coordinator sở hữu file này. Bổ sung `spec.md`, `spec-v2-research-session.md` (giữ nguyên).
Phạm vi: BID, MBB. Nguồn chính: IR chính thức BIDV/MB; HOSE/SSC/VSD chỉ đối chiếu; KBS/VCI là cross-check, không thay tài liệu gốc.
Không LLM/OCR trả phí. Không deploy. Ghi chỉ DB test `btc_eqx_test`.

Baseline (19/10, `evidence/equity-expansion/v3/baseline-coverage.txt`): 5 tài liệu/mã (2025Q3–2026Q2 + FY2025),
KBS 6 chỉ số 1 kỳ/mã, MBB 4 NET_PROFIT issuer-report, BID 0 fact gốc.

## 1. Pipeline & ranh giới

```
discover → download/version → extract (text layer → OCR) → normalize → validate → persist → derive → coverage/snapshot
```
- Python thực thi các stage (OCR/PDF ở Python). Entry point duy nhất: `ai/equity_pipeline.py run --symbol {BID|MBB}
  --mode {backfill|incremental} --run-id <guid> [--budget N] [--max-docs N] [--dry-run]`. Ghi trực tiếp PostgreSQL
  (psycopg2) theo từng tài liệu trong transaction riêng; trạng thái DB chính là checkpoint (resume = làm tiếp phần chưa xong).
- BE điều phối: chạy subprocess đó (admin endpoint + worker theo lịch), giữ trạng thái run, timeout/cancel, lock, đọc
  coverage, phục vụ facts/snapshot. Không web request kéo dài: POST trả 202 + runId, GET để theo dõi.
- Lock chống chạy đè: (1) partial unique index `EquityPipelineRuns(Symbol) WHERE Status='running'`; (2) Python lấy
  `pg_try_advisory_lock(hashtext('equity-docs:'||symbol))` trong suốt run; không lấy được ⇒ thoát mã 75 (busy).
  Run "running" có heartbeat cũ > 15 phút ⇒ BE đánh dấu `failed` (`stale-heartbeat`) trước khi chạy run mới.

## 2. Schema (coordinator — migration `EquityDocumentPipeline`)

- `EquitySourceDocuments` (có sẵn): thêm cột `ExtractionState` không cần — trạng thái suy ra từ bảng extraction.
  Revision: cùng URL bytes mới ⇒ document MỚI (hash mới) với `SupersedesDocumentId` = bản trước, `Revision`+1; bản cũ giữ.
  Cùng hash nhiều URL ⇒ 1 document + nhiều `EquitySourceDocumentLocations`.
- `EquitySourceDocumentLocations`: DocumentId, SourceUrl, Provider, ListingTitle, ListingDate?, FirstSeenAtUtc,
  LastSeenAtUtc, LastFetchedAtUtc, HttpEtag?, HttpLastModified?; unique (DocumentId, SourceUrl).
- `EquitySourceAttempts`: RunId?, Symbol, Source, TargetPeriod?, DocType?, Consolidation?, Url?, AttemptedAtUtc,
  Outcome ∈ listed|not-listed|downloaded|unchanged|changed|http-error|rate-limited|timeout|parse-error|budget-exhausted|
  too-large, HttpStatus?, Detail. Mỗi khoảng thiếu trong coverage trỏ về attempt cụ thể.
- `EquityDocumentExtractions`: DocumentId, ExtractorVersion, Method ∈ text-layer|ocr|mixed, Status ∈
  succeeded|partial|failed, PageCount, PagesOcr, ArtifactPath (text/bảng trung gian, tương đối `EquityDocuments:RootPath`),
  QualityJson, Error?, StartedAtUtc, FinishedAtUtc; unique (DocumentId, ExtractorVersion) ⇒ không OCR lại file không đổi
  cùng version.
- `EquityFinancialFacts` (insert-only core; chỉ `Status`/`ValidationJson` được cập nhật, trigger bảo vệ):
  FactKey unique, Symbol, MetricCode, MetricDefinitionVersion, MetricGroup ∈ balance-sheet|income-statement|cash-flow|
  credit-quality|deposits-funding|capital|shares, Statement ∈ balance-sheet|income-statement|cash-flow|note|derived,
  PeriodLabel ("2026Q2","2026H1","2026-9M","2026"), PeriodType ∈ quarter|ytd|half-year|annual|point-in-time,
  PeriodStart?, PeriodEnd, FiscalYear, Consolidation ∈ consolidated|separate, Value (numeric, NOT NULL — không thấy
  thì không có row), Unit ∈ VND|percent|shares|count|ratio, Scale, RawLabel, RawValueText, RawUnitText, ColumnRole ∈
  current|comparative|cumulative-current|cumulative-comparative|derived, PageNumber?, Location?, DocumentId,
  ExtractionId?, Status ∈ extracted|validated|review-needed|quarantined, ValidationJson, IsDerived, DerivationJson?
  (formula, input FactKeys, derivation version), AvailableAtUtc (= availability của document; derived = max inputs),
  CreatedAtUtc.
  CHECK: derived ⇔ (DerivationJson not null ∧ ExtractionId null ∧ ColumnRole='derived'); không derived ⇒ PageNumber>0 ∧
  ExtractionId not null ∧ RawValueText not empty.
- `EquityPipelineRuns`: Symbol, Mode ∈ backfill|incremental, Trigger ∈ worker|admin|cli, Status ∈ running|succeeded|
  partial|failed|cancelled, StartedAtUtc, HeartbeatAtUtc, FinishedAtUtc?, CountsJson, CheckpointJson, Error?, LogTail?.

## 3. Metric catalog & quy tắc chuẩn hóa

- `ai/equity_facts/metric_catalog.json` (worker B): mỗi metric: code, group, statement, định nghĩa, nhãn neo tiếng Việt
  (biến thể theo issuer), unit, sign convention, ghi chú khác biệt định nghĩa. BE đọc catalog qua
  `EquityFacts:MetricCatalogPath` (mặc định `../ai/equity_facts/metric_catalog.json`, như EvidenceCatalog) để biết
  metric kỳ vọng khi tính coverage.
- Ưu tiên: TOTAL_ASSETS, TOTAL_EQUITY, EQUITY_ATTRIBUTABLE_PARENT, CUSTOMER_LOANS_GROSS, LOAN_LOSS_PROVISION_BALANCE,
  CUSTOMER_LOANS_NET, CUSTOMER_DEPOSITS, INTEREST_INCOME, INTEREST_EXPENSE, NET_INTEREST_INCOME, FEE_INCOME (net fee),
  OPERATING_EXPENSES, CREDIT_PROVISION_EXPENSE, PROFIT_BEFORE_TAX, PROFIT_AFTER_TAX, PROFIT_AFTER_TAX_PARENT,
  NPL_GROUP2, NPL_GROUP3_5 (hoặc từng nhóm 3/4/5), CHARTER_CAPITAL, SHARES_OUTSTANDING, CASA/CAR khi issuer công bố.
  Cash-flow: NET_CASH_FROM_OPERATING (+ khác khi có).
- Không đánh đồng: LNST hợp nhất ≠ LNST thuộc CĐ mẹ; dư nợ gross ≠ net; quý ≠ lũy kế; % ≠ fraction; hợp nhất ≠ riêng.
- Derived (`IsDerived`): Q4 = FY − 9M chỉ khi cùng consolidation/unit/document lineage hợp lệ; quarter từ YTD hiệu;
  tỷ lệ tự tính (NPL ratio, LDR, ROE, NIM…) chỉ khi đủ input và định nghĩa rõ, ghi `formula`. Chỉ số issuer công bố
  (`Statement=note`, ColumnRole current) tách biệt với tự tính (MetricCode hậu tố `_CALC`). PB không lưu như fact quý.
- Validation: quan hệ kế toán (tổng tài sản = nợ + vốn; NII = thu nhập lãi − chi phí lãi; PBT − thuế = PAT; PAT =
  PAT_parent + minority; tổng nợ nhóm = dư nợ gross…), đối chiếu cột comparative của báo cáo sau, đối chiếu KBS/VCI chỉ
  làm cờ. Mâu thuẫn ⇒ `quarantined`/`review-needed`, không đưa vào chuỗi validated. Không đoán số mờ.

## 4. Coverage & snapshot (worker C)

- `GET /api/equities/{symbol}/fundamentals/coverage`: lưới period (2016Q1…latest Q/H1/FY) × metric group × consolidation,
  mỗi ô stage cao nhất đạt được (`not-attempted|not-listed|discovered|downloaded|extracted|validated|persisted`) +
  counts + reason + attemptId/documentId. Có tổng hợp before/after được lưu trong evidence.
- `GET /api/equities/{symbol}/financial-facts?metric=&periodType=&from=&to=&asOf=&status=` (public, market-only):
  chuỗi nhiều kỳ, mỗi fact kèm document ref (url, sha256, page, location), extraction status, availability.
- Snapshot/readiness (`equity-research-snapshot-v1` additive): `fundamentalsSeries` (validated facts ≤ as-of, nhiều kỳ),
  `fundamentalsCoverage` (tóm tắt + gaps có reason), document extraction status; as-of không thấy document/revision chưa
  biết (AvailableAtUtc ≤ cutoff; revision mới chỉ hiện sau khi first-seen).
- Pipeline runs: `POST /api/equities/{symbol}/admin/documents/pipeline?mode=backfill|incremental` (AdminGuard; 202 +
  runId; 409 `PIPELINE_BUSY`), `GET /api/equities/{symbol}/documents/pipeline-runs` (public, không chứa secret).
- Worker: `EquityDocuments:PipelineEnabled` (mặc định false), daily incremental poll (giờ cấu hình, mặc định 18:30 VN),
  timeout backfill 6 h / incremental 45 phút, cancel = kill process tree, cô lập lỗi từng mã.

## 5. Tiêu chí xong (đo bằng dữ liệu — xem yêu cầu user §5, 12 mục)
Evidence ở `evidence/equity-expansion/v3/`. Không gọi "PASS" khi chỉ tải được PDF.

## 6. Bổ sung sau inventory nguồn (A0, 2026-10-19 — `evidence/equity-expansion/v3/a0-source-inventory.md`)

- Phạm vi tải: consolidated + separate cho mọi kỳ Q/H1/FY 2016Q1→kỳ mới nhất đã niêm yết (BID 124, MBB 121 mục). Thứ tự:
  consolidated 8 quý gần nhất → consolidated lịch sử → separate. Trích xuất facts: consolidated bắt buộc; separate làm sau
  nếu còn thời gian, coverage báo đúng.
- Gaps đã xác minh (không phải "scan"): BID separate Q4 2017/2018 không niêm yết ở đâu; MBB FY2016 separate + FY2017 cả hai
  đã công bố (thông báo MB) nhưng file không còn hosted; MBB H1 2017 chưa xác minh. Q3/2026 chưa công bố (~30/10).
- Restatement BID là tài liệu riêng trong disclosure feed (FY2016, 2021, 2022, 2024): lưu như document `financial-statement`
  với cờ restatement trong ExtractionJson/Title; facts của nó là FactKey riêng. Chuỗi series chọn, cho mỗi
  (metric, period, periodType, consolidation), fact validated có AvailableAtUtc ≤ cutoff mới nhất (restated thắng bản gốc
  khi đã biết); cả hai giữ lại.
- Ngày công bố: BID có ngày (day-only ⇒ 00:00 ICT ngày sau). MB không có ngày đáng tin ⇒ AvailableAt = first-seen trừ khi
  có ngày từ thông báo MB (quét notices, ~185 request) / Last-Modified server (chỉ sau giữa 2019) — ghi rõ basis; không
  dùng "save date" của MB.
- Version detection: BIDV URL có version token (thiếu token ⇒ trang lỗi HTML ⇒ luôn kiểm magic `%PDF-`); MB URL cố định
  có thể bị ghi đè ⇒ so size liệt kê + ETag/Last-Modified + hash; khác ⇒ document mới (Revision+1, Supersedes).
- OCR: BID scan toàn bộ (2016 ~80 dpi); MBB có text layer nhiễu ⇒ ưu tiên OCR riêng, text layer chỉ để đối chiếu; phải áp
  rotation của trang. Statement thường ở trang 2–8 (quý), 10–15 (H1/FY); notes 50–90 trang ⇒ phân loại trang trước, OCR
  đầy đủ chỉ trang liên quan.

## 7. Interface Python giữa orchestrator (A) và extraction (B)

```python
# ai/equity_facts/__init__.py  (worker B)
EXTRACTOR_VERSION: str            # bump khi logic trích xuất đổi ⇒ chỉ khi đó mới OCR lại

def extract_document(conn, document: dict, root: Path, *, run_id: str | None, heartbeat: Callable[[], None]) -> dict:
    """document = row EquitySourceDocuments (Id, Symbol, StoragePath, ReportingPeriod, DocType, Consolidation,
    AuditStatus, PublishedAtUtc, FirstSeenAtUtc, Title, ExtractionJson…). Idempotent: (DocumentId, EXTRACTOR_VERSION)
    succeeded/partial ⇒ {"skipped": True}; failed ⇒ xoá row failed rồi chạy lại. Ghi EquityDocumentExtractions +
    EquityFinancialFacts trên `conn` KHÔNG commit (orchestrator commit/rollback theo từng document). Artifact trung gian
    (ảnh trang, text, bảng, JSON words) dưới root/{SYM}/extractions/{sha256}/{EXTRACTOR_VERSION}/. Gọi heartbeat() định kỳ.
    Trả {status, method, pageCount, pagesOcr, facts, validated, reviewNeeded, quarantined}."""

def derive_and_cross_validate(conn, symbol: str, *, run_id: str | None) -> dict:
    """Sau khi persist: derived facts (Q4 = FY − 9M, quý từ YTD, *_CALC ratios) idempotent theo FactKey; đối chiếu cột
    comparative giữa báo cáo; chỉ được cập nhật Status/ValidationJson của facts có sẵn. Không commit."""
```
Orchestrator `ai/equity_pipeline.py` (A): advisory lock; tạo/cập nhật EquityPipelineRuns (heartbeat ≤ 60 s, CountsJson
theo stage); discover → download (EquitySourceDocuments + Locations + Attempts) → với mỗi document chưa có extraction
cho EXTRACTOR_VERSION: transaction riêng → extract_document → commit; lỗi ⇒ rollback + ghi attempt/extraction failed ở
transaction riêng; → derive_and_cross_validate → commit; exit code 0/2/75/1 theo spec §1. `--mode incremental`: chỉ
listing + tài liệu mới/đổi + extraction còn thiếu; không tải/OCR lại file không đổi.
