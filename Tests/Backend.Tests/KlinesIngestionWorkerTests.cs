using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Backend.Tests;

public class KlinesIngestionWorkerTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static KlinesIngestionWorker CreateWorker(KlinesIngestionOptions? options = null)
    {
        var opt = options ?? new KlinesIngestionOptions();
        return new KlinesIngestionWorker(
            null!,
            NullLogger<KlinesIngestionWorker>.Instance,
            OptionsFactory.Create(opt));
    }

    [Fact]
    public async Task FindGapsAsync_NoData_ReturnsSingleGap()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();

        var gaps = (await worker.FindGapsAsync(db, "BTCUSDT", "1h", 0, 3_600_000 * 5, 50, default)).ToList();

        Assert.Single(gaps);
        Assert.Equal(0, gaps[0].StartMs);
        Assert.Equal(3_600_000 * 5, gaps[0].EndMs);
        Assert.Equal(6, gaps[0].MissingCount);
    }

    [Fact]
    public async Task FindGapsAsync_WithHeadAndTailGaps_ReturnsHeadAndTail()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();

        // Có dữ liệu từ 10h đến 12h, thiếu đầu (0..9h) và cuối (13h..24h).
        db.Klines.AddRange(
            CreateKline("1h", 3_600_000 * 10),
            CreateKline("1h", 3_600_000 * 11),
            CreateKline("1h", 3_600_000 * 12));
        await db.SaveChangesAsync();

        var gaps = (await worker.FindGapsAsync(db, "BTCUSDT", "1h", 0, 3_600_000 * 24, 50, default)).ToList();

        Assert.Equal(2, gaps.Count);
        Assert.Contains(gaps, g => g.StartMs == 0 && g.EndMs == 3_600_000 * 9); // head
        Assert.Contains(gaps, g => g.StartMs == 3_600_000 * 13 && g.EndMs == 3_600_000 * 24); // tail
    }

    [Fact]
    public async Task FindGapsAsync_WithInternalGap_ReturnsInternalGapSortedBySize()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();

        // Có dữ liệu 0,1,2,4,5,8 (giờ). Thiếu 3 và 6,7.
        db.Klines.AddRange(
            CreateKline("1h", 0),
            CreateKline("1h", 3_600_000),
            CreateKline("1h", 3_600_000 * 2),
            CreateKline("1h", 3_600_000 * 4),
            CreateKline("1h", 3_600_000 * 5),
            CreateKline("1h", 3_600_000 * 8));
        await db.SaveChangesAsync();

        var gaps = (await worker.FindGapsAsync(db, "BTCUSDT", "1h", 0, 3_600_000 * 8, 50, default)).ToList();

        // Gap 6-7 lớn hơn gap 3 nên đứng đầu.
        Assert.Equal(2, gaps.Count);
        Assert.Equal(3_600_000 * 6, gaps[0].StartMs);
        Assert.Equal(3_600_000 * 7, gaps[0].EndMs);
        Assert.Equal(2, gaps[0].MissingCount);

        Assert.Equal(3_600_000 * 3, gaps[1].StartMs);
        Assert.Equal(3_600_000 * 3, gaps[1].EndMs);
        Assert.Equal(1, gaps[1].MissingCount);
    }

    [Fact]
    public async Task InsertBatchAsync_SkipsExistingAndInsertsNew()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();

        db.Klines.Add(CreateKline("1h", 0));
        await db.SaveChangesAsync();

        var batch = new List<KlineDto>
        {
            CreateKlineDto(0),
            CreateKlineDto(3_600_000),
            CreateKlineDto(3_600_000 * 2)
        };

        var inserted = await worker.InsertBatchAsync(db, "BTCUSDT", "1h", batch, default);

        Assert.Equal(2, inserted);
        Assert.Equal(3, await db.Klines.CountAsync());
    }

    [Fact]
    public async Task BackfillGapAsync_FillsSmallGapInSingleRequest()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();
        var fakeBinance = new CountingFakeBinance("1h");

        // Gap từ 0 đến 5h -> cần 6 nến, một request là đủ.
        var (inserted, requestsUsed) = await worker.BackfillGapAsync(
            fakeBinance, db, "BTCUSDT", "1h", 3_600_000,
            0, 3_600_000 * 5, requestBudget: 10, default);

        Assert.Equal(1, requestsUsed);
        Assert.Equal(6, inserted);
        Assert.Equal(6, await db.Klines.CountAsync());
    }

    [Fact]
    public async Task BackfillGapAsync_SplitsLargeGapAcrossMultipleRequests()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();
        var fakeBinance = new CountingFakeBinance("1h");

        // Gap 2500 nến 1h, budget 2 request -> mỗi request tối đa 1000 nến.
        var (inserted, requestsUsed) = await worker.BackfillGapAsync(
            fakeBinance, db, "BTCUSDT", "1h", 3_600_000,
            0, 3_600_000L * 2_499, requestBudget: 2, default);

        Assert.Equal(2, requestsUsed);
        Assert.Equal(2_000, inserted);
        Assert.Equal(2_000, await db.Klines.LongCountAsync());
        Assert.All(fakeBinance.Calls, c => Assert.True(c.limit <= 1_000));
    }

    [Fact]
    public async Task BackfillGapAsync_StopsWhenBudgetExhausted()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();
        var fakeBinance = new CountingFakeBinance("1h");

        // Gap 2500 nến, budget 1 request -> chỉ insert 1000 nến đầu.
        var (inserted, requestsUsed) = await worker.BackfillGapAsync(
            fakeBinance, db, "BTCUSDT", "1h", 3_600_000,
            0, 3_600_000L * 2_499, requestBudget: 1, default);

        Assert.Equal(1, requestsUsed);
        Assert.Equal(1_000, inserted);
    }

    private static Kline CreateKline(string timeframe, long openTimeMs) => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = timeframe,
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 3_599_999,
        Open = 64000m,
        High = 64100m,
        Low = 63900m,
        Close = 64050m,
        Volume = 1m,
        QuoteVolume = 1m,
        TradeCount = 1,
        TakerBuyVolume = 0.5m,
        TakerBuyQuoteVolume = 0.5m
    };

    private static KlineDto CreateKlineDto(long openTimeMs) => new()
    {
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 3_599_999,
        Open = 64000m,
        High = 64100m,
        Low = 63900m,
        Close = 64050m,
        Volume = 1m,
        QuoteVolume = 1m,
        TradeCount = 1,
        TakerBuyVolume = 0.5m,
        TakerBuyQuoteVolume = 0.5m
    };

    private class CountingFakeBinance : IBinanceKlinesService
    {
        private readonly string _interval;

        public int CallCount { get; private set; }
        public List<(long? start, long? end, int limit)> Calls { get; } = new();

        public CountingFakeBinance(string interval)
        {
            _interval = interval;
        }

        public Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
            string symbol = "BTCUSDT",
            string interval = "1h",
            int limit = 48,
            long? startTimeMs = null,
            long? endTimeMs = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Calls.Add((startTimeMs, endTimeMs, limit));

            var intervalMs = _interval switch
            {
                "1h" => 3_600_000L,
                "1m" => 60_000L,
                _ => 3_600_000L
            };

            // Giả lập giới hạn 1000 nến của Binance.
            var effectiveLimit = Math.Min(limit, 1_000);
            var start = startTimeMs ?? 0;
            var end = endTimeMs ?? (start + (effectiveLimit - 1) * intervalMs);

            var list = new List<KlineDto>();
            for (var t = start; t <= end && list.Count < effectiveLimit; t += intervalMs)
            {
                list.Add(new KlineDto
                {
                    OpenTimeMs = t,
                    CloseTimeMs = t + intervalMs - 1,
                    Open = 64000m,
                    High = 64100m,
                    Low = 63900m,
                    Close = 64050m,
                    Volume = 1m,
                    QuoteVolume = 1m,
                    TradeCount = 1,
                    TakerBuyVolume = 0.5m,
                    TakerBuyQuoteVolume = 0.5m
                });
            }

            return Task.FromResult<IReadOnlyList<KlineDto>>(list);
        }

        public Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(
            string interval = "1h", int limit = 48, CancellationToken cancellationToken = default)
            => GetKlinesAsync("BTCUSDT", interval, limit, cancellationToken: cancellationToken);

        public Task<string> BuildTechSummaryAsync(
            string interval = "1h", int limit = 48, CancellationToken cancellationToken = default)
            => Task.FromResult("fake summary");
    }
}
