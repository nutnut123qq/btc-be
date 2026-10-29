# EQX deploy handoff — Tài liệu chuẩn bị — CHƯA THỰC THI PRODUCTION

Cập nhật: 2026-10-21 (bản đầu 2026-10-18). Không có lệnh nào dưới đây đã chạy trên production. Mọi bằng chứng là trên DB
test (`btc_eqx_test`, `btc_eqx_migtest`) và stack local — **không** phải bằng chứng production. Mọi bước ghi production
(commit/push, deploy, migrate, backfill, bật worker) cần user duyệt.

## 0. Hiện trạng production (read-only check 2026-10-18 do user cung cấp; chưa kiểm lại sau ngày đó)

- Backend HEAD `2f047ff`; DB `bitcoin_analyst` chưa có `InstrumentCandles` hay bảng equity nào; migration mới nhất
  `20261001183725_AddEmbeddingProvenanceAndWorkerCycleStats`.
- Production thiếu 4 commit backend đã có ở local master: `5e35951`, `28ca11d`, `482948e` (MBB lane), `209c85d`
  (MBB guard) + toàn bộ diff EQX chưa commit. ai/frontend tương tự (ai `0be0858`, frontend `5f9e5c8`, `7f0e767` +
  diff EQX).

## 1. Đưa code lên từng repo (cần quyền commit/push của user)

`backend/`, `ai/`, `frontend/` là 3 repo git riêng; thư mục gốc `D:\code\btc` (chứa `tools/`, `data/`, `evidence/`)
**không** phải repo. Danh sách file chính xác: `git status --short` mỗi repo tại thời điểm commit. Đề xuất 1 commit EQX
mỗi repo (hoặc tách v1/v2/v3 nếu muốn review dễ hơn).
- backend: services/controllers/models/tests EQX (gồm v3 `Services/EquityDocumentPipeline.cs`,
  `Services/EquityFinancialFactsService.cs`, `Services/EquityResearchServiceCollectionExtensions.cs`,
  `Controllers/EquityResearchController.cs`), `Data/Calendars/vn-hose-non-trading-days.json`, migration (mục 2),
  `contracts/openapi.json` (apiContractVersion `2026-10-equity-lane-v1`), `ops/preflight-equity-journal.sql`,
  `Program.cs`, `appsettings.json`, docs `docs/plans/equity-expansion/*`.
- ai: `trading_config.py`, `equity_base_rates.py`, `equity_llm.py` (retired, disabled), `research_snapshot.py`,
  `equity_documents.py`, `equity_pipeline.py`, `equity_facts/**` (gồm `metric_catalog.json`),
  `vietnam_market_calendar.py` + call sites, `main.py`, tests, `skills/investment-research/**`, `requirements.txt` +
  `requirements.lock.txt` (mục 4.1).
- frontend: Equity* components/libs, `/co-phieu`, `contracts/openapi.json` + `src/lib/generated/api-contract.d.ts`
  (copy từ backend — pitfall #42), `apiContract.ts` expected `2026-10-equity-lane-v1` (giữ bản cũ trong rollout set).
- Ngoài repo, phải chép riêng lên server: `tools/tesseract/` (~180 MB, mục 4.2). Không chép `evidence/**`.
  `data/equity-documents/**` (tài liệu gốc + artifact) không bắt buộc: production DB chưa có row tài liệu nào, backfill
  (mục 5.2) tự discover/tải lại từ IR issuer.
- Thứ tự deploy: **backend + migration trước**, rồi ai, rồi frontend (Vercel auto khi push). FE mới chịu được BE cũ
  (surface mới 404 ⇒ unavailable), nhưng BE mới phải có migration trước khi chạy (pitfall #13: ProductionLike không
  auto-migrate ⇒ crash-loop nếu thiếu cột).

## 2. Migration (10, theo thứ tự, mới hơn production head `20261001183725_AddEmbeddingProvenanceAndWorkerCycleStats`)

```
1  20261015181547_AddInstrumentCandles
2  20261016015246_AddEquityPositions
3  20261016044350_AddMbbSignalObservations
4  20261016065010_TightenMbbObservationFillCoherence
5  20261016072541_EquityExpansionLane
6  20261016155506_TightenEquityForecastOutcomeCoherence
7  20261018081208_EquityResearchSessionLane      (NULL-safe CHECK + trigger write-once + bảng calendar/cash/docs/notes)
8  20261018093632_MarketCalendarCoverage         (cột coverage cho MarketCalendarDays; XOÁ các row calendar dẫn xuất —
                                                  backend phải khởi động lại để startup sync tạo lại kèm coverage)
9  20261019084223_EquityDocumentPipeline         (v3: locations, attempts, extractions, financial facts + trigger
                                                  core-immutable, pipeline runs)
10 20261019091543_TightenFinancialFactProvenance (v3: PageNumber IS NOT NULL cho fact không derived)
```
Quy trình (remote, theo AGENTS.md pitfall #15/#16/#25/#31):
1. `ops/guarded-backup.ps1` (backup + verify) — bắt buộc trước migrate.
2. Migrate tới #6 `20261016155506_TightenEquityForecastOutcomeCoherence` bằng `dotnet ef database update <id>` với
   connection string có `Command Timeout=3600` (dựng từ `Get-BackendConnectionString` trong `ops/common.ps1`, thay giá
   trị timeout; không dùng `migrate.ps1` mặc định 30 s — pitfall #16).
3. Preflight trước #7: `psql -v ON_ERROR_STOP=1 -At -f ops/preflight-equity-journal.sql` — mọi count phải = 0 (trên
   production các bảng vừa tạo nên dự kiến 0). Count ≠ 0 ⇒ DỪNG, điều tra; migration không sửa/xoá dữ liệu để tự pass.
4. Migrate nốt #7 → #10 (`20261018081208_EquityResearchSessionLane`, `20261018093632_MarketCalendarCoverage`,
   `20261019084223_EquityDocumentPipeline`, `20261019091543_TightenFinancialFactProvenance`), cùng connection string.
5. `dotnet ef migrations has-pending-model-changes` = không có. Khởi động lại backend rồi kiểm
   `SELECT count(*), min("CoverageFromDate"), max("CoverageToDate") FROM "MarketCalendarDays"` (dự kiến 80 row,
   2020-01-01..2026-12-31). Bảng rỗng ⇒ Python fail closed `calendar-unavailable` cho conditions/base-rates/replay — đừng
   chạy job equity trước bước này.

Bằng chứng hiện có — **chuỗi đủ 10 migration từ mốc production: UNVERIFIED** (chưa có
`evidence/equity-expansion/v3/migration-chain-10.log`):
- `evidence/equity-expansion/v2-migration-chain.log`: chỉ #1–#8 trên DB trống tại mốc production — up, preflight 0,
  3 trigger write-once tạo đúng, down về mốc production, up lại OK.
- #9 và #10 mới chỉ apply tăng dần lên `btc_eqx_test` (`evidence/equity-expansion/v3/migration-apply.log` ghi #9;
  `__EFMigrationsHistory` của `btc_eqx_test` có cả #10). Chưa chạy chuỗi #1–#10 liền mạch từ production head.

## 3. Config

### 3.1 Equity lane (v1/v2) — bật sau migrate, theo thứ tự

| Key | Giá trị | Ghi chú |
|---|---|---|
| `MarketCalendar:SyncOnStartup` | true (mặc định) | đồng bộ file calendar → `MarketCalendarDays`; kiểm `GET /api/market-calendar/vn-hose` |
| `MbbIngestion:Enabled` | true | cho phép gọi VCI/KBS (mặc định false) |
| `EquityIngestion:Enabled` | **false** tới khi backfill xong & kiểm | worker hậu phiên; heartbeat không gây restart (pitfall #40) |
| `EquityLlm:Enabled` / `EQUITY_LLM_ENABLED` | **false** (giữ) | lớp LLM trong app đã ngừng; không cấu hình `ANTHROPIC_API_KEY` |
| AI env `BACKEND_BASE_URL` | URL backend nội bộ | cho `research_snapshot.py` |

### 3.2 Pipeline tài liệu → facts (v3) — tên/mặc định đúng như `EquityDocumentsOptions` / `EquityFactsOptions`

Đường dẫn tương đối được resolve theo content root của backend.

| Key | Mặc định trong code | Ghi chú |
|---|---|---|
| `EquityDocuments:RootPath` | `../data/equity-documents` | tài liệu gốc + artifact trích xuất; đặt tuyệt đối trên server, ngoài repo |
| `EquityDocuments:PythonPath` | `../ai/venv/Scripts/python.exe` | interpreter có đủ package mục 4.1 |
| `EquityDocuments:PipelineScript` | `../ai/equity_pipeline.py` | |
| `EquityDocuments:ExtraEnv` | `{}` | env thêm cho subprocess, vd `EQX_TESSERACT_EXE`, `EQX_TESSDATA_DIR`; các key `DB_*`, `PG*`, `EQUITY_DOCUMENTS_ROOT` bị bỏ qua (backend tự đặt) |
| `EquityDocuments:PipelineEnabled` | `false` | bật worker incremental hằng ngày (mục 5.3) |
| `EquityDocuments:PipelineRunAtLocal` | `"18:30"` | HH:mm Asia/Ho_Chi_Minh |
| `EquityDocuments:PipelineSymbols` | `["BID","MBB"]` | chạy tuần tự, lỗi cô lập theo mã |
| `EquityDocuments:BackfillTimeoutMinutes` | `360` | |
| `EquityDocuments:IncrementalTimeoutMinutes` | `45` | |
| `EquityDocuments:StaleHeartbeatMinutes` | `15` | run `running` có heartbeat cũ hơn ⇒ `failed` (`stale-heartbeat`) trước run mới |
| `EquityDocuments:WorkerBudget` | null | `--budget` cho run của worker (null ⇒ mặc định script: incremental 60 request) |
| `EquityDocuments:WorkerMaxDocs` | null | `--max-docs` cho run của worker |
| `EquityDocuments:WorkerExtractScope` | `consolidated` | `consolidated` \| `all` |
| `EquityDocuments:WorkerStandalone` | `false` | chỉ có tác dụng khi `BackgroundWorkers:Enabled=false`: đăng ký RIÊNG worker tài liệu (vẫn thoát ngay nếu `PipelineEnabled=false`) |
| `EquityFacts:MetricCatalogPath` | `../ai/equity_facts/metric_catalog.json` | catalog metric kỳ vọng cho coverage |

(`appsettings.json` hiện chỉ ghi `PipelineEnabled=false`, `PipelineRunAtLocal`, `BackfillTimeoutMinutes`,
`IncrementalTimeoutMinutes`; còn lại lấy mặc định code.)

Cách backend chạy pipeline: subprocess `<PythonPath> -I -X utf8 <PipelineScript> run --symbol X --mode M --run-id G
[--budget N] [--max-docs N] [--dry-run] --extract-scope S`. Credential DB chỉ truyền qua env `DB_HOST/DB_PORT/DB_USER/
DB_NAME/DB_PASS` (từ `ConnectionStrings:DefaultConnection`), không có trên command line; root tài liệu qua
`EQUITY_DOCUMENTS_ROOT`. Tài khoản chạy backend cần quyền đọc/ghi `RootPath` (tài liệu + artifact) và đọc
`MetricCatalogPath`, `PythonPath`, `PipelineScript`, thư mục Tesseract. `-I` bỏ qua `PYTHONPATH`/user site-packages ⇒
package phải nằm trong interpreter/venv đó.

## 4. Dependency runtime của pipeline tài liệu

### 4.1 Python (pin trong `ai/requirements.txt`, khoá trong `ai/requirements.lock.txt`)

`pypdfium2==5.14.0`, `pillow==12.3.0`, `rapidocr==3.9.2`, `onnxruntime==1.30.0`, `opencv-python==4.14.0.94`,
`shapely==2.1.2`, `pyclipper==1.4.0`, `omegaconf==2.3.1` (+ phụ thuộc trong lock: `flatbuffers==25.12.19`,
`antlr4-python3-runtime==4.9.3`), `pypdf==6.14.2`, `psycopg2-binary>=2.9.0,<3.0.0` (không pin chính xác). Trên Linux OpenCV cần `libGL` (vd gói `libgl1`).

### 4.2 Tesseract 5.5.3 portable (`tools/README.md`)

- Bản portable conda-forge (~180 MB, chỉ `vie`/`eng`/`osd`), không cài hệ thống; nguồn, sha256 và cách cài tái lập ở
  `tools/README.md`.
- Licence: Tesseract/tessdata Apache-2.0, leptonica BSD-2-Clause; thư viện kèm `libarchive` bản gpl link `lzo` (GPL-2.0)
  ⇒ dùng nội bộ được, xem lại trước khi phân phối lại thư mục này.
- `ai/equity_facts/ocr.py` mặc định dùng `<workspace>/tools/tesseract/Library/bin/tesseract.exe` và
  `<workspace>/tools/tesseract/share/tessdata` (`<workspace>` = thư mục cha của `ai/`; trên máy dev là
  `D:\code\btc\tools\tesseract\Library\bin\tesseract.exe` và `D:\code\btc\tools\tesseract\share\tessdata`).
  Layout khác ⇒ đặt `EQX_TESSERACT_EXE` / `EQX_TESSDATA_DIR` (qua `EquityDocuments:ExtraEnv` hoặc env của tiến trình
  backend). CPU: `EQX_TESS_THREADS` (mặc định 1), `EQX_OCR_THREADS` (onnxruntime, mặc định 4).
- Thiếu binary ⇒ extraction từng tài liệu ghi `failed` (thử lại ở run sau), không im lặng.

### 4.3 Dung lượng

`data/equity-documents` trên máy dev 2026-10-21: ~2,6 GB (248 PDF gốc + artifact OCR). Không nằm trong repo.

## 5. Backfill / recompute (bounded, cần user duyệt)

### 5.1 Dữ liệu thị trường / context (v1/v2)

1. Admin `POST /api/equities/{SYM}/admin/backfill?timeframe=1d` cho MBB, BID, VCB, CTG, VNINDEX; MBB và BID thêm `1h` (BID 1h từ EQTECH-1).
   Tham số mặc định đã bounded (page 2000, MaxRequestsPerCycle 10, delay 500 ms). VNINDEX lịch sử từ 2010-01-01.
2. `POST /api/equities/{MBB|BID}/admin/recompute` (MBB 1h+1d, BID 1h+1d). Backfill 1h xong thì recompute lại 1h, vì nến cũ vừa nạp chưa có chỉ báo (đã chạy cho BID 2026-10-30).
3. `POST /api/equities/{SYM}/admin/context/sync` (VCI events, KBS fundamentals; 2 request/mã).
4. `POST /api/equities/{MBB|BID}/admin/base-rates/rebuild` (AI build artifact; protocol đóng băng; dự kiến vẫn abstained).
5. Revision: VCI/KBS rewrite lịch sử ở mỗi GDKHQ ⇒ sync phát hiện ⇒ refetch toàn lịch sử (atomic) ⇒ base-rates
   `artifact-stale-revision` ⇒ rebuild.
Chỉ sau khi 1–4 kiểm xong mới cân nhắc `EquityIngestion:Enabled=true`.

### 5.2 Tài liệu → facts: backfill một lần qua backend

Mọi admin endpoint cần header `X-Admin-Key`.
- `POST /api/equities/{BID|MBB}/admin/documents/pipeline?mode=backfill` (thêm `&extractScope=all` nếu muốn trích cả báo
  cáo riêng — chưa từng chạy, xem mục 7). Trả 202 + run; 409 `PIPELINE_BUSY` nếu mã đó đang có run; 503
  `PIPELINE_UNAVAILABLE` nếu thiếu interpreter/script.
- Thời lượng: ≈2,5–3 giờ OCR CPU mỗi mã; lock theo mã nên 2 mã chạy song song được. DB là checkpoint: run bị ngắt (hoặc
  kết thúc `partial` vì hết request budget/WAF MB) ⇒ gọi lại `mode=backfill` sẽ resume phần còn thiếu, không OCR lại
  tài liệu đã có extraction cùng version.
- Theo dõi: `GET /api/equities/{SYM}/documents/pipeline-runs?take=5`; huỷ:
  `POST /api/equities/{SYM}/admin/documents/pipeline/{runId}/cancel` (kill process tree; chỉ run do chính backend đó
  giám sát).
- Kiểm: `GET /api/equities/{SYM}/fundamentals/coverage`, `GET /api/equities/{SYM}/financial-facts?metric=...` — so với
  số ở mục 7.
- Lệnh tái lập trên DB test (admin endpoint + CLI `python -I ai/equity_pipeline.py run --symbol BID --mode incremental`
  với env `DB_*`): `evidence/equity-expansion/v3/README.md`.

### 5.3 Incremental hằng ngày qua worker

- Bật `EquityDocuments:PipelineEnabled=true` **sau** khi backfill 5.2 xong và kiểm. Worker chỉ được đăng ký khi
  `BackgroundWorkers:Enabled=true` (mặc định), hoặc khi `BackgroundWorkers:Enabled=false` +
  `EquityDocuments:WorkerStandalone=true`.
- Lịch: mỗi ngày tại `PipelineRunAtLocal` (mặc định 18:30 giờ VN), chạy `mode=incremental` lần lượt từng mã trong
  `PipelineSymbols`. Khi khởi động, worker chạy slot đến hạn gần nhất một lần nếu mã đó chưa có run worker nào kết thúc kể
  từ slot đó; mỗi mã tối đa một run worker mỗi slot; mã đang bận thì bỏ qua.
- Incremental trên DB test: ~7 s (BID) / ~72 s (MBB) (`evidence/equity-expansion/v3/idempotency-incr.log`). Incremental
  cũng trích nốt tài liệu còn thiếu extraction nhưng bị giới hạn 45 phút ⇒ không dùng worker thay cho backfill.
- Heartbeat `EquityDocumentPipelineWorker` chỉ là liveness tiến trình; không nằm trong `ExpectedWorkers` của health ⇒
  không kích watchdog restart (pitfall #40).

## 6. Kết quả kiểm thử worker local (2026-10-21)

**PASS** — chi tiết: `evidence/equity-expansion/v3/worker-local-test.md` (+ `worker-local-test-db.txt`, `-run-counts.txt`,
`-logs.txt`, `-fault-injection.sql`). Chỉ DB test `btc_eqx_test`, backend copy riêng port 5298, cấu hình qua env tiến trình
(`BackgroundWorkers:Enabled=false`, `EquityDocuments:WorkerStandalone=true`, `PipelineEnabled=true`,
`PipelineRunAtLocal=08:10`, `WorkerBudget=60`, `WorkerMaxDocs=5`, `EquityLlm:Enabled=false`); appsettings và đồng hồ không đổi.
- Khởi động: worker tự chạy catch-up slot 2026-10-20 08:10 — BID `86c45fad…` và MBB `6123ec5b…` succeeded (trigger `worker`).
- Restart trong slot đã xong: không run mới (heartbeat `Idle`, skipped 2).
- Slot lịch 2026-10-21 08:10 do scheduler tự kích: BID `5f7be8de…` **failed** (exit 75 — advisory lock BID bị giữ có chủ đích
  để mô phỏng tiến trình khác), MBB `31304f58…` vẫn chạy và succeeded ⇒ lỗi một mã không chặn mã kia; heartbeat `Partial`.
  Restart sau đó: không run mới (run failed vẫn tính là run của slot — không retry trong cùng slot).
- Không trùng tài liệu/location/extraction/fact (248/248/không đổi/73.765 trước = sau); file không đổi không tải/OCR lại
  (`unchangedNoFetch` 127 BID / 20 MBB, `extract.pending=0`); request 2–9/60. Chỉ nhật ký `EquitySourceAttempts` tăng.
- Thay đổi code đi kèm: `EquityDocuments:WorkerStandalone` (mục 3.2) + test `EquityDocumentWorkerRegistrationTests` (3 case).
- Hai quan sát từ lần kiểm thử này **đã sửa** (2026-10-21, snapshot method `equity-research-snapshot-method-v2`; xem
  `evidence/equity-expansion/v3/snapshot-v2-fix.md`):
  - (1) Trước đây run worker không đổi dữ liệu vẫn đổi `snapshotSha256` ở as-of cũ (attempt ghi sau as-of lọt vào
    `fundamentalsCoverage`). Nay attempt chỉ được tính khi `attemptedAtUtc ≤ asOf`, và tài liệu available sau as-of không còn
    làm đổi lưới coverage quá khứ.
  - (2) Trước đây readiness `latest-expected-bar-stored` so tại cutoff (nến cuối đã lưu), nên snapshot trễ 2 phiên vẫn "ready".
    Nay check so với phiên lẽ ra đã finalized tại requestedAsOf và fail-closed khi as-of nằm ngoài coverage calendar.
  - Snapshot id đổi so với v1 cho cùng dữ liệu. Note cũ giữ id v1 mà chúng đã được xác minh lúc ghi.

## 7. Hiện trạng dữ liệu trên DB test `btc_eqx_test` (2026-10-21)

Nguồn: `evidence/equity-expansion/v3/final-data-summary.txt`, `coverage-after4-matrix.txt`, `b-round4-proof.md`.
- Tài liệu đã tải 248 (BID 127 = 63 hợp nhất + 61 riêng + 3 bản trình bày lại hợp nhất/riêng chung một PDF; MBB 121 =
  61 hợp nhất + 60 riêng), kỳ 2016 → 2026Q2.
- Extraction hiện hành `eqx-facts-v4` / derive `eqx-derive-v4` cho báo cáo **hợp nhất**: BID 66 tài liệu (65 succeeded,
  1 partial — bản trình bày lại 2021), MBB 61/61. Báo cáo **riêng** đã tải (BID 61, MBB 60) nhưng **chưa trích xuất**.
- Facts in (v4): BID 8.219 validated / 317 review-needed / 153 extracted / 40 quarantined; MBB 8.575 / 65 / 244 / 27.
  Derived: BID 2.814 validated / 698 review-needed; MBB 3.190 / 30.
- Coverage hợp nhất (ô = kỳ × nhóm metric): BID **331/341** ô `persisted`, MBB **318/341**. 8 quý gần nhất 2024Q3–2026Q2
  đủ cho cả hai mã. Ô còn thiếu: 5 ô 2026Q3/mã (chưa công bố); BID 3 ô validated-thiếu-metric + 2 ô chỉ extracted; MBB
  14 ô validated-thiếu-metric (chủ yếu PROFIT_AFTER_TAX_PARENT 2016–2019 không in trong báo cáo) + 4 ô chỉ extracted.
- `persisted` = mọi metric **kỳ vọng trong catalog** (`ai/equity_facts/metric_catalog.json`; metric optional/derived
  không tính) có fact validated. Coverage đo theo catalog, **không** phải mọi dòng/thuyết minh của báo cáo.
- Hai loại kiểm khác nhau:
  - Tự động (mọi fact): `validated` = qua đẳng thức kế toán trong tài liệu (tổng tài sản = nợ + vốn, NII = thu − chi lãi,
    PBT − thuế = PAT…) hoặc khớp fact validated của báo cáo khác (cột so sánh). Mâu thuẫn ⇒ `review-needed`/`quarantined`.
  - Thủ công (mẫu): reviewer đối chiếu trang PDF **54 fact** (v2, `review-v3.md`) + **68 giá trị** (v3,
    `review-v3-delta.md`), 0 sai. v4 không được review thủ công lại; nó được chặn bằng regression gate v2/v3→v4 trên toàn
    bộ 127 tài liệu (`b-round4-proof.md`). Phần lớn fact chưa từng được người đối chiếu với PDF.

## 8. Health / coverage checks sau deploy

- `GET /api/equities` (catalog v2 + ingestion status), `/api/equities/{SYM}/data-quality` (gaps: market-wide vs
  symbol-missing; unverified non-trading days in range), `/research-readiness` (per purpose), `/base-rates` (status +
  reasons), `/context` (RS benchmarks không `stale`), `/api/market-calendar/vn-hose` (80 ngày, version
  `vn-hose-2026-10-18`), `/fundamentals/coverage`, `/documents/pipeline-runs`. `ops/status.ps1` không có component
  equity mới ⇒ watchdog không bị ảnh hưởng.
- Smoke CLI: `python ai/research_snapshot.py readiness --symbol BID --as-of <ISO>` và
  `python ai/research_snapshot.py export --symbol BID --as-of <ISO> --out-dir <dir>`.

## 9. Rollback

- Code: revert commit EQX từng repo, deploy lại (FE mới chịu được BE cũ nên không bắt buộc FE trước).
- Schema/dữ liệu: **rollback = restore backup (bước 2.1)**. Không dựa vào `Down` khi đã có dữ liệu: `Down` của
  `EquityResearchSessionLane` khôi phục CHECK cũ và thất bại với dữ liệu v2 (vd metric `NET_PROFIT`, forecast
  `session-research`), `Down` của `EquityExpansionLane` thất bại khi có observation BID, và mọi `Down` xoá bảng equity
  (mất dữ liệu ingest/journal/notes/tài liệu/facts/pipeline runs). `Down` chỉ được kiểm trên DB trống cho #1–#8
  (`v2-migration-chain.log`); `Down` của #9/#10 chưa kiểm.

## 10. Hạn chế / blocker trước production

- **Calendar hết coverage 2026-12-31** (coverage 2020-01-01..2026-12-31): trước ngày đó phải bổ sung lịch nghỉ 2027
  (thông báo HOSE) vào `Data/Calendars/vn-hose-non-trading-days.json` và deploy; quá hạn ⇒ decision `wait` (kể cả khi chạm
  stop-loss), forecast/note forward bị từ chối, readiness technical not-ready. 2021–2023 còn `unverified-legacy` (đính kèm
  HOSE không còn phục vụ) và 2022-01-03 chỉ có nguồn báo chí.
- **PIT:**
  - 19 tài liệu MBB kỳ 2016H1–2019Q1 (10 hợp nhất, 9 riêng) không có ngày công bố đáng tin ⇒ availability = first-seen
    (2026-10), nên as-of quá khứ không thấy các tài liệu này.
  - Giá equity là latest-adjusted ⇒ tái dựng giá point-in-time chỉ `partial` hoặc không thể (`spec-v2-research-session.md`).
  - Trích xuất là hồi cứu: `Status`/`ValidationJson` của fact là trạng thái hiện tại (validation cross-document dùng cả
    báo cáo về sau), không phải trạng thái as-of; as-of chỉ lọc theo availability của tài liệu.
- Báo cáo riêng (separate) chưa trích xuất.
- Nợ nhóm 1–5 và dự phòng có trong facts hợp nhất (tỷ lệ nợ xấu/bao phủ chỉ là derived); tỷ lệ NPL/CAR/CASA do ngân hàng
  tự in thường không có trong báo cáo quý ⇒ không có chuỗi đầy đủ.
- Quant base-rates abstained 4/4 (không tự đổi ngưỡng).
- LLM trong app tắt (`EquityLlm:Enabled=false`, `EQUITY_LLM_ENABLED` ≠ 1).
- Chuỗi 10 migration từ production head chưa được kiểm liền mạch (mục 2).
