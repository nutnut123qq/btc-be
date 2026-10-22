# EQX task board / status

Coordinator: session chính. Worker không spawn con. Status: TODO | WIP | REVIEW | PASS | BLOCKED | ABSTAINED.

| ID | Wave | Owner | Depends | Write boundary | DoD / gate | Status |
|---|---|---|---|---|---|---|
| EQX-W0-PROBE | 0 | worker probe | — | ai/scripts/probe_equity_market_data.py, evidence/equity-expansion/w0-* | mapping/unit/ts/coverage/basis + context endpoints có evidence | PASS |
| EQX-00 | 0 | coordinator | — | InstrumentCatalog/Models, trading_config.py, filter attribute, Data/Equity*.cs, AppDbContext, migration EquityExpansionLane, Options/EquityIngestionOptions | build xanh; migration apply trên `btc_eqx_test` | PASS (eqx-00-migration-test.log) |
| EQX-A1 | 1 | worker A (BE) | 00 | backend Services/Controllers/Tests (trừ file coordinator), Program.cs | §2 routes P0 + alias, §4 worker, observation multi-symbol, PG tests | PASS (650/650, PG 12/12, a1-real-ingest.md) |
| EQX-B1 | 1 | worker B (AI) | 00 | ai/equity_base_rates.py, ai/main.py (endpoint), tests, run_mbb_replay --symbol BID | §5 protocol + pytest; report OOS thật | PASS — gate FAIL cả 4 config ⇒ unvalidated (p1-base-rates-build.log) |
| EQX-C1 | 1 | worker C (FE) | spec §2/§9 | frontend src/components/Equity*, src/lib/equity*, app routes, tests, e2e | §9 P0 panels + base-rates + context (mocks) + unit/e2e | PASS (199 unit, 44 e2e) |
| EQX-A2 | 2 | worker A | A1, B1 | BE base-rates read, context collector (VCI events, KBS fundamentals) + admin import, RS, forecast journal + outcome resolver, worker seam | §5 read, §6, §8 | PASS (673/673 lúc đó) |
| EQX-B2 | 2 | worker B | B1 | ai/equity_llm.py + FastAPI /api/equity-llm/run (Claude API qua httpx, web search, schema validate, mock tests) | §7 AI side | PASS mock (390 pytest); live pending: không có ANTHROPIC_API_KEY |
| EQX-C2 | 2 | worker C | C1, A2 | FE journal/scoring, LLM runs, dossier; live-stack E2E + screenshots | §9 states + e2e | PASS mock (209 unit, 50 e2e) |
| EQX-A3 | 3 | worker A | A2, B2 | BE dossier + llm-runs + scoring | §7, §8 | PASS (689/689) |
| EQX-INT | 3 | coordinator | all | contracts/openapi BE→FE, types, gates, live flow | §10 | PASS (final-*.log) |
| EQX-REV | 3 | reviewer read-only | INT | — | findings report | PASS — 3 review: review-be-ai.md, review-fixes.md, review-fe.md |

## Trạng thái cuối (2026-10-17)

- Fix sau review: BE #1 #3 #4 #5 #11 #13 #14 (worker A), AI #2 #12 (worker B), #6 migration `20261016155506_TightenEquityForecastOutcomeCoherence`
  + PG test (coordinator), FE #1–#8 (worker C); timeout AI 240 s < BE 270 s < proxy FE 290 s.
- Gate cuối (sau mọi fix): BE 697/697, PG 16/16, không pending model change; AI 398; FE contract check, 211 unit, tsc, eslint 0 lỗi,
  build, Playwright mock 53 pass/9 skip; live-stack 2/2 (`final-live-e2e.log`). Stack test đã tắt; vị thế BID/MBB trên DB test = 0.
- Chưa sửa (ghi lại): xem báo cáo cuối của coordinator.

## v2 — data correctness + research session (2026-10-18, spec-v2-research-session.md)

| ID | Owner | Write boundary (file thật) | DoD | Status |
|---|---|---|---|---|
| V2-00 | coordinator | Data/EquityLaneRecords.cs, Data/EquityPositionAccount.cs, Data/AppDbContext.cs, Migrations/20261018081208_EquityResearchSessionLane*, ops/preflight-equity-journal.sql, Options/EquityLlmOptions.cs, Controllers/EquityDossierController.cs (LLM guard), ai/main.py + ai/equity_llm.py (LLM_DISABLED), spec-v2 | build xanh; preflight 0; migrate btc_eqx_test; LLM disabled trước upstream | PASS (v2-a2-preflight-migrate.log; AI llm tests 49) |
| V2-A | worker A (BE) | backend Services/{VietnamMarketCalendar,MbbDecisionService,MbbDataQualityService,EquityContextService,MbbSignalObservationService,EquityForecastJournalService,...}.cs, Services/MarketCalendar*.cs (mới), Data/Calendars/vn-hose-non-trading-days.json (mới), Controllers/{MbbPositionController,EquitiesController,MarketCalendarController(mới)}.cs, Services/Models/{MbbPositionModels,EquityResearchModels}.cs, Program.cs, appsettings.json, Options/MbbIngestionOptions.cs, Tests/* (trừ file B tạo) | A1 BE + calendar nguồn chính thức, A2 PG tests + trigger writer paths, A3 cash pool/budget/CA conversion, A4 RS stale, LLM-disabled tests | PASS (760/760, PG 21/21; v2-a1-calendar.md) |
| V2-B | worker B (Python + BE research endpoints) | ai/vietnam_market_calendar.py, ai/research_snapshot.py (mới), ai/tests/unit/test_research_snapshot*.py, test calendar; backend file MỚI: Services/EquityResearchSnapshotService.cs, Services/EquityResearchNoteService.cs, Services/EquityResearchServiceCollectionExtensions.cs, Controllers/EquityResearchController.cs, Services/Models/EquityResearchSnapshotModels.cs, Tests/Backend.Tests/EquityResearchSnapshotTests.cs | Python calendar từ DB fail-closed; readiness/snapshot/notes/source-documents endpoints; CLI export/readiness/journal; rebuild sau A1 | PASS (468 pytest; v2-b-rebuild.md, snapshots/) |
| V2-C | worker C (collector → skill → FE) | ai/equity_documents.py (mới), ai/tests/unit/test_equity_documents.py, data/equity-documents/** (ngoài repo), ai/skills/investment-research/** (mới), frontend src/components/Equity*.tsx, src/lib/equity*.ts, src/lib/api.ts, tests/*, e2e/* | ≥1 BCTC gốc/mã có provenance; skill dùng được bằng path; FE bỏ nút LLM + quỹ tiền/budget + RS stale | PASS (10 tài liệu gốc; skill v1.1; FE 221 unit/57 e2e) |
| V2-INT/REV/FT | coordinator + reviewer + forward-tester | contracts, gates, live flow, deploy-handoff.md | §D | PASS (review-v2.md, review-v2-delta.md, 2 forward-test, live 4/4) |

### v2 trạng thái cuối (2026-10-18)
- Migration mới: `20261018081208_EquityResearchSessionLane`, `20261018093632_MarketCalendarCoverage` (đã apply btc_eqx_test).
- Gate cuối: BE 760/760, PG 21/21, no pending model; AI 468; FE 221 unit, tsc, eslint 0 lỗi, build, Playwright mock 57 (11 skip
  live) + live 4/4; contract additive vs HEAD; chuỗi 8 migration từ mốc production up/down trên DB trống.
- Forward-test skill: 2 agent context mới (không key: BID forward + MBB hồi cứu; có key: MBB vị thế + ghi note forward).
- DB test thay đổi đợt này: +2 migration; MarketCalendarDays 80 row; 10 EquitySourceDocuments (BID 5 có ngày công bố BIDV);
  4 EquityFundamentals issuer-report (MBB NET_PROFIT); 2 research notes bất biến (BID retrospective smoke, MBB forward
  forward-test); recompute MBB/BID; artifact base-rates rebuilt; vị thế/quỹ/budget về rỗng.

## v3 — document → facts pipeline (2026-10-19, spec-v3-document-pipeline.md)

Skill `orchestrate` không có trong catalog/path môi trường này (đã kiểm ~/.claude/skills, plugins) ⇒ fallback: quy trình coordinator thủ công như v1/v2.

| ID | Owner | Write boundary | DoD | Status |
|---|---|---|---|---|
| V3-00 | coordinator | spec-v3, Data/EquityLaneRecords.cs (+5 entity), Data/AppDbContext.cs, Migrations/20261019084223_EquityDocumentPipeline* (+trigger facts core-immutable; doc period H1/H2), 20261019091543_TightenFinancialFactProvenance | build; apply btc_eqx_test | PASS (v3/migration-apply.log) |
| V3-A0 | worker A | evidence/v3/a0-source-inventory.* (research only) | inventory IR 2016→nay, availability matrix, attempts | PASS (a0-source-inventory.md: BID 124/126, MBB 121/126 kỳ 2016→2026H1; 7 gaps có lý do) |
| V3-B0 | worker B | tools/**, ai/requirements*, evidence/v3/b0-* (research + install) | chọn OCR local miễn phí, đo độ chính xác số | PASS (b0-ocr-eval.md: hybrid Tesseract+RapidOCR 414/414 số trên 10 trang) |
| V3-A | worker A | ai/equity_documents.py, ai/equity_pipeline.py (mới), tests tương ứng | discover/download/version/attempts/locations, orchestrator + lock + heartbeat | PASS (248 docs; drills; idempotency) |
| V3-B | worker B | ai/equity_facts/** (mới), tests | facts có page/location, validation, quarantine, derived | PASS (eqx-facts-v4) |
| V3-C | worker C | backend Services/EquityDocumentPipeline*.cs, coverage/facts endpoints, snapshot/readiness additive, worker, Program.cs/appsettings, tests | job runner + API + snapshot | PASS code (779/780: lỗi env OpenRouter); chờ dữ liệu thật |
| V3-INT/REV | coordinator + reviewer | backfill thật, manual validation vs PDF, revision/restart tests, gates | §5 tiêu chí dữ liệu | TODO |

### v3 trạng thái khi user yêu cầu dừng (2026-10-20)
- Đã xong: inventory nguồn; discovery/download 248 tài liệu (BID 127, MBB 121) có attempts/locations; OCR local; extraction v2 cho
  127/127 tài liệu consolidated; incremental + backfill lặp lại idempotent; drill revision/restart; reviewer 54/54 fact khớp PDF;
  fix as-of quá khứ (C) + ô comparative-only; gate BE 787 + PG 23, AI 616, FE 221 unit/57 e2e (trước các sửa cuối của C/B).
- BỊ HỦY GIỮA CHỪNG: re-extraction `eqx-facts-v3` (run cancelled qua admin endpoint): BID 17/66, MBB 14/61 tài liệu đã có v3;
  phần còn lại vẫn dùng v2 (coverage/series chọn extraction mới nhất từng tài liệu ⇒ nhất quán). Chạy lại POST
  `/admin/documents/pipeline?mode=backfill` sẽ resume phần còn thiếu.
- Chưa làm: coverage-after3, reviewer delta cho v3, gate cuối sau sửa của C (as-of, comparative-only) và B (v3), trích xuất báo cáo
  riêng (separate), báo cáo cuối.
- Backend test và mọi tiến trình OCR đã tắt.

### v3 trạng thái cuối (2026-10-21)
- Extraction hiện hành `eqx-facts-v4` / `eqx-derive-v4` cho 127/127 tài liệu consolidated (BID 66: 65 succeeded + 1 partial; MBB 61/61).
- Coverage consolidated `persisted`: BID 0 → 228 (v1) → 304 (v2) → 322 (v3) → **331/341**; MBB 0 → 224 → 273 → 313 → **318/341**.
  8 quý gần nhất 2024Q3–2026Q2: không còn ô thiếu cho cả hai mã. Gaps còn lại: 5 ô 2026Q3/mã (chưa công bố), MBB
  PROFIT_AFTER_TAX_PARENT 2016–2019 (không in trong báo cáo — reviewer xác minh), tiền gửi/vốn ở vài bản scan 2016–2019.
- Facts v4: BID 8.219 validated / 40 quarantined / 317 review-needed (đa số cột so sánh); MBB 8.575 / 27 / 65.
- Review dữ liệu: 54/54 (v2) + 68/68 (v3) giá trị đối chiếu trang PDF đúng; regression gate v2/v3→v4 toàn bộ 127 tài liệu.
- Separate statements: tải về đủ (BID 61, MBB 60) nhưng CHƯA trích xuất (phạm vi ưu tiên consolidated) — việc chưa làm.
- Migration v3: `20261019084223_EquityDocumentPipeline`, `20261019091543_TightenFinancialFactProvenance` (apply btc_eqx_test).
