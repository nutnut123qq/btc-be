# MBB technical expansion — kế hoạch triển khai

Ngày: 2026-10-15.
Trạng thái: đã được user duyệt triển khai bằng workflow subagent vào 2026-10-15. User chọn dùng chung ứng dụng; sau MBB-00, user chốt MBB production dùng `1h` và `1d`, bỏ khung `4h` vì không có nến 4h native phù hợp từ nguồn đã khảo sát.

## 1. Mục tiêu và phạm vi

Một ứng dụng cho BTC và MBB. MBB có dữ liệu, phân tích kỹ thuật, nghiên cứu, dự đoán và đề xuất hành động riêng, phục vụ người dùng đã đầu tư khoảng 5 triệu VND vào MBB.

- Backfill toàn bộ lịch sử MBB có thể lấy hợp lệ từ nguồn đã chọn; công bố first/last bar, coverage và gaps cho từng timeframe. Không hứa có mọi nến từ ngày niêm yết khi nguồn không cung cấp.
- Tính lại chỉ báo, mẫu nến, cấu trúc giá, thống kê, historical analog, dataset và model từ dữ liệu MBB. Không dùng nến, mẫu lịch sử, model weights, scaler, ngưỡng đã tối ưu hay evidence BTC làm kết quả MBB.
- Dùng lại code thuật toán và component khi semantics phù hợp; tham số mặc định của một công thức là cấu hình ban đầu, không phải tham số đã được chứng minh cho MBB.
- Khung phân tích mục tiêu: `1h` và `1d`. Không coi hai khung tương quan là hai bằng chứng độc lập, không hứa đây là tổ hợp chính xác nhất.
- Không dùng giá/nến của mã khác làm feature, training hoặc historical analog MBB trong scope này.
- Tin tức, báo cáo tài chính, sentiment, funding, open interest, liquidation, Binance order execution không thuộc MBB.
- Sự kiện quyền/cổ tức/chia tách vẫn thuộc chuẩn hóa dữ liệu và hạch toán vị thế. Đây là dữ liệu cần để tính đúng, không phải phân tích cơ bản.
- Chỉ hỗ trợ quyết định thủ công; không tự đặt lệnh, margin hoặc bán khống.

## 2. Evidence trong code hiện tại

Các path dưới đây tính từ workspace D:/code/btc:

| Điểm cần thay đổi | Code hiện tại |
| --- | --- |
| Production chỉ cho BTC | backend/Services/ProductionSymbolPolicy.cs; ai/trading_config.py; frontend/src/lib/marketScope.ts |
| Ingestion gọi Binance và danh sách BTC cố định | backend/Services/KlinesIngestionWorker.cs |
| Đồng hồ nến liên tục | backend/Services/Timeframes.cs; backend/Services/KlineGapQuery.cs |
| Loader yêu cầu duration chính xác bằng milliseconds của timeframe | ai/current_technical_conditions.py::_load_inputs |
| Cache thiếu symbol vì trước đây chỉ có BTC | backend/Services/CurrentConditionsService.cs; ai/current_technical_conditions.py |
| Current conditions API chỉ nhận timeframe | backend/Controllers/ResearchCurrentConditionsController.cs |
| Capability registry mô tả BTC | backend/Services/TechnicalCapabilityRegistry.cs |
| Có thể dùng lại phép tính replay | backend/Services/TechnicalReplayLayerService.cs |
| Kline có khóa symbol/timeframe/open time nhưng nhiều field riêng Binance | backend/Data/Kline.cs; backend/Data/AppDbContext.cs |
| Contract và execution assumptions riêng BTC | ai/research_contract.py; ai/execution_engine.py; ai/prediction_service.py |
| FE có scope singleton và giao diện Binance | frontend/src/lib/marketScope.ts; frontend/src/components/MarketScreen.tsx; frontend/src/components/BinanceTradingScreen.tsx |

Không chỉ mở whitelist MBB: phải kiểm tra toàn bộ caller và job ghi dữ liệu của policy trước khi bật một capability.

## 3. Gate dữ liệu và quy ước timeframe — MBB-00 đã chốt

Tài liệu đã tham khảo:

- https://vnstocks.com/docs/vnstock/v5/thi-truong-market
  - Tài liệu ghi VCI không hỗ trợ trực tiếp 4H.
  - Lịch sử intraday và quota có giới hạn tùy gói/nguồn.
- https://fpts.com.vn/ho-tro-khach-hang/giao-dich-chung-khoan/huong-dan-giao-dich-co-phieu/quy-dinh-giao-dich/quy-dinh-giao-dich-cua-hose/
  - Lịch phiên, nghỉ trưa, ATO/ATC và các quy định giao dịch cần đối chiếu tại lúc triển khai.

MBB-00 đã lấy mẫu thật bằng `ai/scripts/probe_mbb_market_data.py`; evidence lưu ở `evidence/mbb-00/provider-probe-vci-kbs.json`:

- VCI `ONE_DAY`: 3,732 nến, `2011-11-01` → `2026-10-15`; `ONE_HOUR`: 3,519 nến, `2023-09-11` → `2026-10-15`; minute chỉ phù hợp probe ngắn hạn.
- KBS `data_day`: 1,692 nến từ 2020-01-02; `data_60P` trả đúng cadence giờ HOSE. KBS dùng làm cross-check, không phải primary history vì daily ngắn hơn và giá cũ làm tròn.
- Cả VCI và KBS đều trả giá điều chỉnh ở lịch sử cũ; VCI giữ số lẻ, KBS làm tròn nên có sai khác nhỏ. Chuỗi canonical v1 là `price_basis=adjusted`.
- Cadence `1h` VCI là 09:00/10:00/11:00/13:00/14:00 giờ Việt Nam. Có anomaly một nến 12:00 ngày 2024-06-20 cần calendar/session validator quyết định chấp nhận hay cách ly.
- Không có nến `4h` native phù hợp. User đã chọn **bỏ khung 4h** thay vì giả lập; MBB production scope v1 là `1h` + `1d`.

Quyết định contract: không tạo nến `4h` tổng hợp, không gọi một nến phiên HOSE là 240 phút, không yêu cầu MBB có `4h` trong policy/dataset/research. Nến `1d` là bối cảnh chính; `1h` là timing/monitoring.

## 4. Thiết kế dự kiến, chốt contract trước code

### Dữ liệu và pipeline

- Hồ sơ instrument tối thiểu: định danh ổn định, symbol, venue, asset class, currency, timezone, calendar và capability/timeframe đang hỗ trợ.
- Dữ liệu/các artifact phân tách theo instrument, timeframe, price basis, dataset revision và calculation/model version ở những nơi các yếu tố này ảnh hưởng kết quả.
- Giữ dữ liệu BTC hiện hữu tương thích. Quyết định dùng chung hay bổ sung bảng lưu nến sau MBB-00; không thêm MBB vào bảng hiện tại nếu phải giả field TakerBuyVolume/TradeCount bằng 0.
- Có giá thực tế và dữ liệu điều chỉnh/hệ số có provenance. Không dùng giá điều chỉnh để giả khớp lệnh hoặc tính sai giá vốn. Việc nguồn sửa lịch sử phải tạo revision và invalidate/rebuild phần dẫn xuất chịu ảnh hưởng.
- Trong backtest, kiểm soát thời điểm thông tin điều chỉnh được biết; nếu nguồn không có lịch sử point-in-time cần ghi giới hạn, không tuyên bố đã loại look-ahead.
- UI/chiến lược tập trung `1h` và `1d`; không tạo khung phụ nếu không có nguồn native và quy ước đã duyệt.
- Calendar xác định expected bars, close/finalization, gaps, freshness và horizon. Nghỉ trưa/overnight/cuối tuần/ngày lễ không phải data gap.
- Phân biệt thị trường đóng cửa, không có giao dịch, tạm ngừng giao dịch, provider lỗi và dữ liệu bị thiếu.
- Backfill chia batch, checkpoint/resume, retry có giới hạn, upsert idempotent; ingest tăng dần sau backfill. Không chạy lại toàn bộ lịch sử mỗi lần cập nhật.

### API và UI

- DTO/OpenAPI là nguồn sự thật; route/field mới dưới đây là đề xuất, chưa phải contract đã tồn tại.
- Bổ sung API danh sách instruments/capabilities và market data dùng định danh mã; giữ route BTC đang phát hành tương thích.
- Current conditions, replay, evidence, prediction, backtest, alert và health phải nhận/trả đúng instrument. Không fallback sang BTC khi MBB thiếu dữ liệu/model.
- Payload nến cần định danh mã, timeframe, currency, price basis, source, thời điểm khả dụng và trạng thái finalization/coverage.
- Payload decision cần as-of chung, timestamp từng khung, phiên bản chiến lược, evidence status, action, lý do, điều kiện kích hoạt/mất hiệu lực và lý do abstain.
- Bổ sung symbol vào cả cache/gate key, request context, frontend state/URL và artifact lookup. Đổi mã phải hủy hoặc bỏ response cũ.
- BTC/MBB dùng chung shell, component phù hợp và cơ chế truy cập hiện có; MBB không mở Binance websocket/ticker/execution.
- UI có loading/empty/error/stale/closed-market/insufficient-history/unvalidated; hiển thị VND/cổ phiếu và thời gian phiên Việt Nam.
- Dữ liệu vị thế cá nhân phải được bảo vệ bằng quyền đọc/ghi hiện có hoặc contract xác thực được chốt; không đưa giá vốn/số dư vào API thị trường công khai.

### Phân tích và quyết định

- Tái tính riêng MBB các module áp dụng được: indicators, candle patterns/sequences, volume anomalies, regime, confirmed structure/SMC, Fibonacci, volume profile ước lượng và confluence.
- Không diễn giải volume profile từ OHLCV là dữ liệu khớp tại từng giá, hoặc SMC là bằng chứng dòng tiền tổ chức.
- Historical analog, archetype/transition, discovery và ML có dataset/evidence/artifact MBB riêng. Scaler, PCA, clusters, calibration và lựa chọn tham số chỉ fit dữ liệu training MBB.
- Kiểm tra feature schema, loại crypto-only fields thay vì điền 0; model loader kiểm tra instrument, feature order, timeframe, price basis và manifest.
- Giả thuyết phối hợp ban đầu: `1d` cho bối cảnh/xu hướng và `1h` cho thời điểm/kích hoạt. Đây là ứng viên cần backtest, không phải trọng số hoặc quy tắc chiến thắng mặc định.
- Tại quyết định `1h` chỉ lấy nến `1d` đã đóng và đã khả dụng; không gắn giá đóng cửa tương lai vào các nến nhỏ trước đó.
- Giữ/mua thêm/giảm/bán/chờ là hành động theo vị thế, tách khỏi nhãn hướng giá. SELL không mở vị thế short. Hệ thống không ép BUY/SELL khi thiếu bằng chứng.
- Vị thế nhập thủ công từ giao dịch hoặc opening position xác nhận: số cổ phiếu, giá vốn, ngày/khả năng thanh toán, phí và tiền khả dụng. Không suy số lượng từ “5 triệu” hoặc coi đó là toàn bộ NAV.
- Khung nến khác thời gian dự định nắm giữ. Form cần thời hạn nắm giữ, ngân sách mua thêm và giới hạn rủi ro trước đề xuất cá nhân hóa về số lượng.
- Tính lô/bước giá, cổ phiếu khả dụng, lịch thanh toán, phí/thuế theo cấu hình thật của người dùng. Không mặc định tiền bán chưa thanh toán có thể mua ngay.
- AI ngôn ngữ chỉ diễn giải dữ liệu có nguồn; không tự tạo xác suất, mức giá hoặc khối lượng giao dịch.

## 5. Task board dự kiến

Các boundary là khu vực dự kiến; spec chi tiết phải liệt kê file thật trước khi chia việc. Chưa giao worker, chưa mở version thi công.

| Task | Depends | Kết quả và write boundary dự kiến | Nghiệm thu |
| --- | --- | --- | --- |
| MBB-00: data feasibility + timeframe | — | Probe nhỏ trong ai/scripts và fixture liên quan; chốt provider/coverage/calendar/bar examples | Payload thật, đối chiếu nguồn, giới hạn lịch sử, quyết định bỏ 4h đã duyệt; không có blocker dữ liệu bị che giấu |
| MBB-01: shared contracts + instrument scope | 00 | BE DTO/policy/contracts; AI research contract; generated FE types | BTC backward compatible; mã lạ bị từ chối; schema nến, model, decision và vị thế thống nhất; migrations được review trước apply |
| MBB-02: ingestion + calendar + quality | 01 | Provider collector tại AI nếu dùng Python; BE storage/migrations/ingestion/quality/health | Backfill-resume idempotent; sample OHLCV đúng; `1h`/`1d` đúng phiên; nguồn lỗi không tạo giá giả; BTC ingestion giữ đúng mã |
| MBB-03: technical recomputation | 02 | BE technical services/indexers/replay; AI conditions/modules | Các module áp dụng được tính riêng MBB; warmup/gap/price revision được xử lý; cache không lẫn BTC; không reset lịch sử chỉ vì nghỉ lễ |
| MBB-04: research + prediction + execution replay | 03 | AI datasets/evaluators/model registry/execution; BE research/backtest/evidence | Walk-forward MBB, không leakage; signal/fill tách biệt; phí/thuế/settlement/trần-sàn được mô hình hóa; verdict kể cả inconclusive có provenance |
| MBB-05: position + decision + alerts | 03; 04 cho claim đã kiểm chứng | BE position/decision/alert DTO, service, persistence; AI chỉ phần inference đã chốt | Giá vốn/PnL theo fixture thật; không bán quá số khả dụng/mua quá tiền; signal chưa validated được gắn nhãn; action và abstention có lý do |
| MBB-06: UI hai mã | 01; tích hợp sau 02–05 | FE shell/routes/API/parsers/chart/position/decision/evidence; e2e hiện có | BTC→MBB→BTC không lẫn response; 3 khung đúng; nhập vị thế và đọc decision trên UI thật; crypto-only modules không xuất hiện ở MBB |
| MBB-07: forward observation + integration | 04–06 | Recorder MBB, scheduler config, tests/e2e/evidence | Ghi tín hiệu đúng thời điểm; không backfill thành quyết định live; fill/outcome không bị bịa; BTC regression và gates pass |

MBB-01 là phần contract dùng chung nên hoàn thành trước các lane phụ thuộc. MBB-06 có thể dựng UI theo DTO đã chốt, nhưng fixture không thay thế nghiệm thu dữ liệu thật. Shared schema/policy không cho nhiều lane sửa đồng thời.

BTC đang có research/promotion riêng trong TASKS.md: không thay cutoff, reserve, artifact hoặc verdict của đợt đó khi triển khai MBB.

## 6. Protocol đánh giá trước khi gắn độ tin cậy

- Đóng băng dataset snapshot, coverage, price basis, cutoff, code version và trial budget trước khi chọn chiến lược.
- Walk-forward theo thời gian, purge/embargo phù hợp horizon; không random split các cửa sổ chồng lấn.
- So ứng viên kết hợp `1h`+`1d` với từng khung riêng trên cùng decision timestamps đủ dữ liệu; không chọn khoảng lịch sử thuận lợi cho một ứng viên.
- Baseline gồm giữ tiền, mua và giữ MBB, quy tắc đơn giản được khai báo trước. Dùng cùng vốn và accounting sự kiện quyền.
- Báo calibration/Brier nếu có xác suất, coverage/abstention, số mẫu/giao dịch, net PnL, drawdown, turnover và uncertainty; không chỉ accuracy/win rate.
- Execution chỉ sớm nhất sau khi tín hiệu khả dụng; nến không chứng minh đã khớp lệnh. Có tình huống partial/unfilled, gap, khóa trần/sàn, T+ và stop không khớp.
- Với OHLCV không rõ thứ tự chạm stop/target trong nến, dùng quy tắc bảo thủ công bố trước hoặc dữ liệu nhỏ hơn; không chọn thứ tự có lợi.
- Định trước ngưỡng predictive/economic/forward gate sau khi biết coverage và horizon, trước khi xem kết quả lựa chọn. Không bịa con số “accuracy mục tiêu” trong plan.
- Mỗi tầng operational/predictive/economic/forward được ghi riêng. Chưa đủ mẫu hoặc không vượt baseline => inconclusive/unvalidated; không ép có model active.
- Thời gian tích lũy forward evidence là thời gian thị trường thật, không tính là có thể hoàn tất trong một lượt code.

## 7. Definition of done cho đợt triển khai

1. Provider, coverage và semantics `1h`/`1d` được kiểm chứng bằng mẫu MBB thật; dữ liệu thiếu được công bố.
2. Backfill và incremental ingestion chạy đúng lịch; raw/adjusted/provenance/revisions đúng; rebuild chỉ tác động MBB cần tính lại.
3. Các module kỹ thuật thuộc scope có kết quả MBB riêng hoặc unavailable với lý do hợp lệ; không dùng dữ liệu, model hay evidence BTC.
4. Prediction/backtest có protocol và artifact tái lập; chưa chứng minh hiệu quả thì không có nhãn đã validated.
5. Vị thế và đề xuất hành động qua kiểm thử về tiền, số lượng, thời gian khả dụng và điều kiện không khớp.
6. UI chung qua flow thật BTC↔MBB, hai khung `1h`/`1d`, phân tích, evidence, vị thế, decision, lỗi nguồn và dữ liệu chưa đủ.
7. Gates/review pass, recorder MBB hoạt động đúng và BTC không regression. Forward validation chưa đủ thời gian được ghi pending, không tuyên bố đã có lợi thế giao dịch.

Không đặt “mô hình chắc chắn thắng” làm tiêu chí hoàn tất phần mềm. Không đóng gate dữ liệu bằng cách âm thầm bỏ timeframe user yêu cầu.

## 8. Gates và cách triển khai trên máy hiện tại

Lệnh dưới đây lấy từ repo/CI, chưa chạy cho thay đổi MBB:

- Backend, cwd backend: `dotnet build Backend.csproj --configuration Release`; `dotnet test Tests/Backend.Tests/Backend.Tests.csproj --configuration Release`.
- Migration áp trên PostgreSQL test, không production: `dotnet tool run dotnet-ef database update`; kiểm tra pending model changes theo CI.
- Backend contract: `./ops/contract.ps1 -Check -NoBuild`; contract-version gate và vulnerability gate theo `.github/workflows/backend.yml`.
- AI, cwd ai: `venv/Scripts/python.exe -m pytest -q`; startup/compile smoke theo `.github/workflows/ci.yml`. `pytest.ini` hiện giới hạn tests/unit.
- FE, cwd frontend: `node scripts/generate-contract-types.mjs --check`; `node --disable-warning=ExperimentalWarning --experimental-strip-types --test tests/*.test.mts`; `node node_modules/eslint/bin/eslint.js`; `node node_modules/typescript/bin/tsc --noEmit`; `node node_modules/next/dist/bin/next build`.
- Playwright: `node node_modules/@playwright/test/cli.js test --workers=1`, sử dụng suite hiện có; screenshot flow và đối chiếu response UI thật.
- Kiểm thử trọng tâm: ranh giới phiên, auction, ngày lễ, điều chỉnh quyền, revision, mixed-symbol/cache, stale response khi đổi mã, higher-timeframe look-ahead, model mismatch, vị thế và settlement.
- Chạy targeted tests trong lúc sửa, integration gates cuối đợt. Trước gate nặng kiểm tra process/RAM; build, pytest, training/backfill và E2E nặng không chạy chồng nhau.
- Review diff/contracts độc lập khi có ủy quyền dùng reviewer; nếu không thì self-review và ghi rõ giới hạn. Không tự spawn agent từ plan này.

## 9. Giới hạn quyền và điểm dừng

- Deliverable của lượt hiện tại chỉ là plan này, không đổi code/schema/runtime hay board của version BTC đang mở.
- Triển khai bắt đầu bằng MBB-00. Nếu dữ liệu `1h`/`1d` không đáp ứng, trả evidence và phương án cụ thể trước khi đổi scope hoặc mua gói dữ liệu.
- Không commit/push/deploy, sửa DB production hoặc bật tác vụ giao dịch theo plan chưa được giao thực hiện.
- Việc cần user cung cấp khi đến phần vị thế: giao dịch/giá vốn/số cổ phiếu, công ty chứng khoán hoặc biểu phí, thời hạn nắm giữ và giới hạn rủi ro. Không cần các thông tin này để khảo sát dữ liệu.
- Ngoài scope: thêm mã khác, news/fundamentals, order execution, margin/short, nền tảng quản lý danh mục tổng quát, đại tu toàn bộ BTC.
