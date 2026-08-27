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

public class FullReindexServiceTests
{
    private static AppDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static IndexingOptions DefaultOptions() => new()
    {
        DefaultBatchSize = 100,
        CandlePatternBatchSize = 100,
        VolumeStatsBatchSize = 100,
        TechnicalIndicatorsBatchSize = 100,
        TechnicalIndicatorWarmupBars = 50,
        MlDatasetWarmupBars = 50,
        PatternSequenceBatchSize = 100,
        WindowVectorBatchSize = 100,
        MlFeatureBatchSize = 100,
        WindowDatasetBatchSize = 100,
        EnableParallelTimeframes = false,
        MaxInMemoryKlines = 100_000
    };

    private static FullReindexService CreateService(AppDbContext db, IndexingOptions? options = null)
    {
        var opt = options ?? DefaultOptions();
        var binance = new FakeBinanceForReindex();
        var patternIndexer = new CandlePatternIndexer(db, binance, OptionsFactory.Create(opt));
        var vectorIndexer = new WindowVectorIndexer(db, OptionsFactory.Create(opt));
        var volumeIndexer = new CandleVolumeIndexer(db, NullLogger<CandleVolumeIndexer>.Instance, OptionsFactory.Create(opt));
        var techIndexer = new TechnicalIndicatorIndexer(db, NullLogger<TechnicalIndicatorIndexer>.Instance, OptionsFactory.Create(opt));
        var sequenceIndexer = new CandlePatternSequenceIndexer(db, NullLogger<CandlePatternSequenceIndexer>.Instance, OptionsFactory.Create(opt));

        var services = new ServiceCollection();
        services.AddSingleton<FullReindexService>(sp => new FullReindexService(
            db,
            patternIndexer,
            vectorIndexer,
            volumeIndexer,
            techIndexer,
            sequenceIndexer,
            sp.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(opt),
            NullLogger<FullReindexService>.Instance));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new FullReindexService(
            db,
            patternIndexer,
            vectorIndexer,
            volumeIndexer,
            techIndexer,
            sequenceIndexer,
            scopeFactory,
            OptionsFactory.Create(opt),
            NullLogger<FullReindexService>.Instance);
    }

    [Fact]
    public async Task ReindexAsync_SingleTimeframe_PopulatesAllTables()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);
        db.Klines.AddRange(CreateKlines("1h", 0, 100));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.ReindexAsync("BTCUSDT", new[] { "1h" }, windowLookbackBars: 5000);

        Assert.Equal("ok", result.Results["1h"].Status);
        Assert.True(result.Results["1h"].Klines > 0);
        Assert.True(result.Totals.CandlePatterns > 0, "Expected CandlePatterns > 0");
        Assert.True(result.Totals.WindowVectors > 0, "Expected WindowVectors > 0");
        Assert.True(result.Totals.VolumeStats > 0, "Expected VolumeStats > 0");
        Assert.True(result.Totals.TechnicalIndicators > 0, "Expected TechnicalIndicators > 0");
        Assert.True(result.Totals.PatternSequences > 0, "Expected PatternSequences > 0");
        Assert.True(result.TotalRowsIndexed > 0);

        Assert.True(await db.CandlePatterns.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.WindowVectors.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.CandleVolumeStats.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.TechnicalIndicators.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.PatternSequences.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
    }

    [Fact]
    public async Task ReindexAsync_CleanupOldData_ReplacesStaleRows()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);

        // Seed Klines cũ.
        db.Klines.AddRange(CreateKlines("1h", 0, 100));
        await db.SaveChangesAsync();

        // Seed dữ liệu phân tích cũ với OpenTimeMs nằm ngoài range của Klines.
        db.CandlePatterns.Add(new CandlePattern
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = 999_999_999_999L,
            Open = 1m,
            High = 2m,
            Low = 0m,
            Close = 1m,
            Volume = 1m,
            PatternType = "StalePattern",
            PatternCategory = "Single",
            TrendDirection = "Sideways",
            CreatedAtUtc = DateTime.UtcNow
        });

        db.WindowVectors.Add(new WindowVector
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            FeatureType = "close",
            WindowSize = 10,
            StartTimeMs = 999_999_999_999L,
            EndTimeMs = 999_999_999_999L,
            Vector = new[] { 0.1f },
            VectorDim = 1,
            VectorNorm = 0.1f,
            Version = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.CandleVolumeStats.Add(new CandleVolumeStats
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = 999_999_999_999L,
            Volume = 1m,
            VolumeSma20 = 1m,
            VolumeAnomalyRatio = 1.0,
            VolumeVsPrevious = 1.0,
            VolumeVsMax10 = 1.0,
            VolumeTrend = "normal"
        });

        db.TechnicalIndicators.Add(new TechnicalIndicator
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = 999_999_999_999L,
            Rsi14 = 50
        });

        db.PatternSequences.Add(new PatternSequence
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            StartTimeMs = 999_999_999_999L,
            EndTimeMs = 999_999_999_999L,
            WindowSize = 3,
            PatternChainJson = "[\"Stale\"]",
            Count = 1
        });

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.ReindexAsync("BTCUSDT", new[] { "1h" }, windowLookbackBars: 5000);

        Assert.Equal("ok", result.Results["1h"].Status);

        // Dữ liệu cũ phải bị xóa.
        Assert.False(await db.CandlePatterns.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.PatternType == "StalePattern"));
        Assert.False(await db.WindowVectors.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.StartTimeMs == 999_999_999_999L));
        Assert.False(await db.CandleVolumeStats.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.OpenTimeMs == 999_999_999_999L));
        Assert.False(await db.TechnicalIndicators.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.OpenTimeMs == 999_999_999_999L));
        Assert.False(await db.PatternSequences.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.StartTimeMs == 999_999_999_999L));

        // Dữ liệu mới phải tồn tại.
        Assert.True(await db.CandlePatterns.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.PatternType != "StalePattern"));
        Assert.True(await db.WindowVectors.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.CandleVolumeStats.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.TechnicalIndicators.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.PatternSequences.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
    }

    [Fact]
    public async Task ReindexAsync_NoData_ReturnsNoDataStatus()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);
        var service = CreateService(db);

        var result = await service.ReindexAsync("BTCUSDT", new[] { "1h" });

        Assert.Equal("no_data", result.Results["1h"].Status);
        Assert.Equal(0, result.TotalRowsIndexed);
    }

    private static List<Kline> CreateKlines(string timeframe, int startIndex, int count)
    {
        var list = new List<Kline>();
        var baseTime = 1_000_000L;
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0) intervalMs = 3_600_000L;

        var random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            var open = 64000m + random.Next(-500, 500);
            var close = open + random.Next(-500, 500);
            var high = Math.Max(open, close) + random.Next(50, 200);
            var low = Math.Min(open, close) - random.Next(50, 200);
            var t = baseTime + (startIndex + i) * intervalMs;

            // Tạo một vài nến đặc biệt để đảm bảo có patterns.
            if (i == 10)
            {
                // Doji với bóng dưới dài (Hammer-like)
                open = 64000m;
                close = 64001m;
                high = 64100m;
                low = 63500m;
            }
            else if (i == 20)
            {
                // Marubozu tăng
                open = 63800m;
                close = 64200m;
                high = close;
                low = open;
            }
            else if (i == 30)
            {
                // Marubozu giảm
                open = 64200m;
                close = 63800m;
                high = open;
                low = close;
            }

            list.Add(new Kline
            {
                Symbol = "BTCUSDT",
                Timeframe = timeframe,
                OpenTimeMs = t,
                CloseTimeMs = t + intervalMs - 1,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = Math.Abs(random.Next(100, 1000)),
                QuoteVolume = Math.Abs(random.Next(1000, 10000)),
                TradeCount = random.Next(100, 1000),
                TakerBuyVolume = Math.Abs(random.Next(50, 500)),
                TakerBuyQuoteVolume = Math.Abs(random.Next(500, 5000))
            });
        }
        return list;
    }

    /// <summary>
    /// Fake Binance dùng cho CandlePatternIndexer DI (chỉ BuildFullAsync mới dùng, FullReindexService gọi IndexAsync).
    /// </summary>
    private class FakeBinanceForReindex : IBinanceKlinesService
    {
        public Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
            string symbol = "BTCUSDT",
            string interval = "1h",
            int limit = 48,
            long? startTimeMs = null,
            long? endTimeMs = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<KlineDto>>(new List<KlineDto>());
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
}
