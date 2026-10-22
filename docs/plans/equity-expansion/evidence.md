# EQX evidence index

Tất cả evidence nằm ở `D:\code\btc\evidence\equity-expansion\` (workspace-level, ngoài 3 repo).

| Item | File | Nội dung |
|---|---|---|
| W0 provider probe | `w0-provider-probe.md` / `.json` | Live VCI/KBS 2026-10-16: mapping, đơn vị, ts, coverage, adjusted basis, nguồn context |
| EQX-00 migration | `eqx-00-migration-test.log` | btc_eqx_test: seed MBB legacy → migrate → legacy giữ, BID/MBB cùng bar, BID 1h/VCB bị chặn |
| A1 real ingest | `a1-real-ingest.md` | btc_eqx_test: MBB 1d/1h, BID/VCB/CTG/VNINDEX 1d; observe; 401/400 samples |
| P0 BID replay | `p0-bid-replay.log`, `replay/sma-cross-20-50-BID-1d-*.json` | 3173 nến thật; verdict inconclusive (không vượt buy-and-hold) |
| P1 base-rates | `p1-base-rates-build.log`, `base-rates/{BID,MBB}-report.json`; artifact `ai/docs/research/evidence/equity-base-rates/*/base-rates.json` | Build từ DB; gate fail 4/4 (kNN không tốt hơn prior; ECE>0.05); rebuild tái lập trùng khớp |
| B2 LLM | `b2-llm.md` | Request shape (redacted), 40 mock tests; live verification pending (no key) |
| C1/C2 screenshots | `c1-bid.png`, `c1-mbb.png`, `c1-bid-locked.png`, `c2-bid-journal-llm.png` | Mocked backend |
| A2/A3 real runs | `a2-research-real-run.md`, `a3-dossier-llm-real-run.md` | Context sync thật, base-rates read, journal, dossier không lộ vị thế, POST llm-runs → 503 unavailable được lưu |
| INT gates | `int-dotnet-test.log` (689/689), `int-dotnet-pg.log` (16/16), `int-pytest.log` (390), `int-fe-unit.log` (209), `int-fe-build.log`, `int-fe-playwright.log` (50 pass/7 skip) | Chạy tuần tự sau contract regen `2026-10-equity-lane-v1` |
| INT live probes | `live-responses/coord-bid-conditions-1d.json` | BID 1d bar 2026-10-16 close 33950 VND adjusted; window 32 bar (holiday-gap); decision freshness chỉ 1d |
| Reviews | `review-be-ai.md`, `review-fixes.md`, `review-fe.md` | 3 reviewer read-only context mới |
| #6 constraint | `fix6-constraint-test.log` | Outcome NULL entry/resolved bar bị CHECK chặn |
| Final gates | `final-dotnet-test.log`, `final-dotnet-pg.log`, `final-pytest.log`, `final-fe-unit.log`, `final-fe-build.log`, `final-fe-playwright.log`, `final-live-e2e.log` | Sau toàn bộ fix |
| Live screenshots | `live-bid-overview.png`, `live-bid-base-rates-context.png`, `live-mbb-1h.png`, `live-btc.png`, `live-bid-admin.png`, `live-llm-unavailable.png`; `live-responses/` | Stack thật trên btc_eqx_test |

## v2 (2026-10-18)

| Item | File | Nội dung |
|---|---|---|
| A1 calendar | `v2-a1-calendar.md`; `backend/Data/Calendars/vn-hose-non-trading-days.json` | Nguồn HOSE (2020, 2024–2026 verified; 2021–2023 unverified-legacy); recompute trước/sau |
| A2 journal | `v2-a2-preflight-migrate.log`, `v2-migration-chain.log` | Preflight 0; chuỗi 8 migration từ mốc production up/down trên DB trống; trigger write-once |
| B rebuild | `v2-b-rebuild.md`, `v2-b-compare.json`, `v2-final-rebuild.log` | Calendar từ DB; base-rates/replay không đổi số (vẫn abstained); conditions window 32→1693+ bar |
| C1 documents | `v2-c1-documents.md`; `D:\code\btc\data\equity-documents\{BID,MBB}\` | 10 BCTC gốc có hash/URL; MBB NET_PROFIT 4 quý (trang 6); BID scan không text |
| Snapshots | `snapshots/` | Export BID/MBB latest + 2026-06-30, fingerprint verified |
| Reviews | `review-v2.md` | 0 critical/high; fix #1 #2 #5 #7 #8 #9 |
| Final gates | `v2-final-dotnet-test.log` (759), `v2-final-dotnet-pg.log` (21), `v2-final-pytest.log` (468), `v2-final-fe-unit.log` (221), `v2-final-fe-build.log`, `v2-final-fe-playwright.log` (57/11 skip) | Sau mọi fix |
| Forward-test fixture | `v2-forwardtest-fixture.log` | MBB lot test để kiểm skill; khôi phục sau |
| Delta review | `review-v2-delta.md` | Sau fix; 2 medium đã xử lý (handoff migration #8; savepoint calendar) |
| Forward-tests | `D:\code\btc\data\research-sessions\BID\20261018-1658-forward\`, `MBB\20261018-1658-forward\`, `MBB\20261018-1705-retrospective\` | report.md, agents/, scripts/, export/; note MBB `session/MBB/1792108800000/f0e364fd` |
| Final PIT/doc | `v2-final-bid-register.log`, `v2-final-pit-check.log` | BIDV publishedDate fill; doc 2026Q2 vắng ở 15/7, có ở 31/8 |
| Final live | `v2-final-live-e2e.log`, `v2-live-*.png` | 4/4 |
