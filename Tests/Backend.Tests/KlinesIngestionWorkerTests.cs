using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task RunCycleAsync_LatestRequestsHaveSeparateBudget()
    {
        var fakeBinance = new CountingFakeBinance("1h");
        var services = new ServiceCollection()
            .AddSingleton<IBinanceKlinesService>(fakeBinance)
            .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()))
            .BuildServiceProvider();
        var worker = new KlinesIngestionWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<KlinesIngestionWorker>.Instance,
            OptionsFactory.Create(new KlinesIngestionOptions
            {
                BackfillStartDate = DateTime.UnixEpoch,
                MaxRequestsPerCycle = 2,
                MaxGapsPerTimeframe = 1
            }));

        await worker.RunCycleAsync(default);

        Assert.Equal(9, fakeBinance.CallCount);
        Assert.All(fakeBinance.Calls.Take(7), call => Assert.Null(call.start));
        Assert.Equal(2, fakeBinance.Calls.Count(call => call.start.HasValue));
    }

    [Fact]
    public async Task RunCycleAsync_LowBudgetRotatesAcrossTimeframesPersistently()
    {
        var fakeBinance = new EmptyHistoricalBinance();
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddSingleton<IBinanceKlinesService>(fakeBinance)
            .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        var worker = new KlinesIngestionWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<KlinesIngestionWorker>.Instance,
            OptionsFactory.Create(new KlinesIngestionOptions { BackfillStartDate = DateTime.UnixEpoch, MaxRequestsPerCycle = 1, MaxGapsPerTimeframe = 1 }),
            timeProvider: clock);

        await worker.RunCycleAsync(default);
        clock.Advance(TimeSpan.FromMinutes(15));
        await worker.RunCycleAsync(default);

        var historical = fakeBinance.Calls.Where(x => x.start.HasValue).ToArray();
        Assert.Equal(["1d", "4h"], historical.Select(x => x.interval).ToArray());
        using var scope = services.CreateScope();
        Assert.Equal("Succeeded", (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunCycleAsync_InternalFailuresPersistFailedHeartbeat()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddSingleton<IBinanceKlinesService>(new ThrowingBinance())
            .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();
        var worker = new KlinesIngestionWorker(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<KlinesIngestionWorker>.Instance,
            OptionsFactory.Create(new KlinesIngestionOptions { BackfillStartDate = DateTime.UnixEpoch, MaxRequestsPerCycle = 1 }));

        await worker.RunCycleAsync(default);

        using var scope = services.CreateScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WorkerHeartbeats.SingleAsync();
        Assert.Equal("Failed", heartbeat.Status);
        Assert.NotNull(heartbeat.LastFailedAtUtc);
    }

    [Fact]
    public async Task RunCycleAsync_HistoricalFailuresConsumeBudgetBackoffAndFailHeartbeat()
    {
        var dbName = Guid.NewGuid().ToString();
        var binance = new HistoricalThrowingBinance();
        var services = new ServiceCollection()
            .AddSingleton<IBinanceKlinesService>(binance)
            .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        var worker = new KlinesIngestionWorker(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<KlinesIngestionWorker>.Instance,
            OptionsFactory.Create(new KlinesIngestionOptions { BackfillStartDate = DateTime.UnixEpoch, MaxRequestsPerCycle = 2, MaxGapsPerTimeframe = 1 }),
            timeProvider: clock);

        await worker.RunCycleAsync(default);

        Assert.Equal(2, binance.Calls.Count(x => x.start.HasValue));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Failed", (await db.WorkerHeartbeats.SingleAsync()).Status);
        var deferred = await db.KlineGapStates.Where(x => x.NextRetryAtUtc != null).ToListAsync();
        Assert.Equal(2, deferred.Count);
        Assert.All(deferred, gap =>
        {
            Assert.Equal(0, gap.AttemptCount);
            Assert.Equal(KlineGapStatuses.Pending, gap.Status);
            Assert.Equal(clock.GetUtcNow().UtcDateTime.AddHours(24), gap.NextRetryAtUtc);
        });
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
    public async Task FindGapsAsync_PartialTailInterval_DoesNotReturnEmptyGap()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();
        db.Klines.Add(CreateKline("1h", 0));
        await db.SaveChangesAsync();

        var gaps = await worker.FindGapsAsync(db, "BTCUSDT", "1h", 0, 1, 50, default);

        Assert.Empty(gaps);
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
        var (inserted, requestsUsed, _, _) = await worker.BackfillGapAsync(
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
        var (inserted, requestsUsed, _, _) = await worker.BackfillGapAsync(
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
        var (inserted, requestsUsed, _, _) = await worker.BackfillGapAsync(
            fakeBinance, db, "BTCUSDT", "1h", 3_600_000,
            0, 3_600_000L * 2_499, requestBudget: 1, default);

        Assert.Equal(1, requestsUsed);
        Assert.Equal(1_000, inserted);
    }

    [Fact]
    public async Task BackfillGapAsync_EmptyResponseStopsCurrentAttempt()
    {
        await using var db = CreateInMemoryDb();
        var worker = CreateWorker();
        var fakeBinance = new CountingFakeBinance("1h", returnEmpty: true);

        var first = await worker.BackfillGapAsync(
            fakeBinance, db, "BTCUSDT", "1h", 3_600_000,
            0, 3_600_000 * 5, requestBudget: 10, default);
        Assert.Equal((0, 1, true, false), first);
        Assert.Equal(1, fakeBinance.CallCount);
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
        private readonly bool _returnEmpty;

        public int CallCount { get; private set; }
        public List<(string interval, long? start, long? end, int limit)> Calls { get; } = new();

        public CountingFakeBinance(string interval, bool returnEmpty = false)
        {
            _interval = interval;
            _returnEmpty = returnEmpty;
        }

        public virtual Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
            string symbol = "BTCUSDT",
            string interval = "1h",
            int limit = 48,
            long? startTimeMs = null,
            long? endTimeMs = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Calls.Add((interval, startTimeMs, endTimeMs, limit));

            if (_returnEmpty)
                return Task.FromResult<IReadOnlyList<KlineDto>>([]);

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
            string symbol = "BTCUSDT", string interval = "1h", int limit = 48, CancellationToken cancellationToken = default)
            => Task.FromResult("fake summary");

        public Task<IReadOnlyList<MarketTickerDto>> Get24hTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MarketTickerDto>>(new List<MarketTickerDto>());

        public Task<IReadOnlyList<MarketTradeDto>> GetRecentTradesAsync(string symbol = "BTCUSDT", int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MarketTradeDto>>(new List<MarketTradeDto>());

        public Task<OrderBookDepthDto> GetOrderBookDepthAsync(string symbol = "BTCUSDT", int limit = 20, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderBookDepthDto { Symbol = symbol });
    }

    private sealed class EmptyHistoricalBinance : CountingFakeBinance
    {
        public EmptyHistoricalBinance() : base("1h") { }

        public override Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
            string symbol = "BTCUSDT", string interval = "1h", int limit = 48,
            long? startTimeMs = null, long? endTimeMs = null, CancellationToken cancellationToken = default)
        {
            if (startTimeMs.HasValue)
            {
                // Record through base, whose empty mode cannot vary by request, then discard result.
                _ = base.GetKlinesAsync(symbol, interval, limit, startTimeMs, endTimeMs, cancellationToken);
                return Task.FromResult<IReadOnlyList<KlineDto>>([]);
            }
            return base.GetKlinesAsync(symbol, interval, limit, startTimeMs, endTimeMs, cancellationToken);
        }
    }

    private sealed class ThrowingBinance : CountingFakeBinance
    {
        public ThrowingBinance() : base("1h") { }
        public override Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
            string symbol = "BTCUSDT", string interval = "1h", int limit = 48,
            long? startTimeMs = null, long? endTimeMs = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("simulated Binance outage");
    }

    private sealed class HistoricalThrowingBinance : CountingFakeBinance
    {
        public HistoricalThrowingBinance() : base("1h") { }
        public override async Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
            string symbol = "BTCUSDT", string interval = "1h", int limit = 48,
            long? startTimeMs = null, long? endTimeMs = null, CancellationToken cancellationToken = default)
        {
            var result = await base.GetKlinesAsync(symbol, interval, limit, startTimeMs, endTimeMs, cancellationToken);
            if (startTimeMs.HasValue)
                throw new HttpRequestException("simulated ranged-request outage");
            return result;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
