using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Backend.Tests;

public class IndexerOptimizationTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
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
        WindowDatasetBatchSize = 100
    };

    [Fact]
    public async Task CandlePatternIndexer_BuildFullAsync_IndexesPatternsAndSkipsExisting()
    {
        await using var db = CreateInMemoryDb();
        var binance = new FakeBinanceKlinesService();
        var indexer = new CandlePatternIndexer(db, binance, OptionsFactory.Create(DefaultOptions()));

        db.Klines.AddRange(CreateKlines("1h", 0, 10));
        await db.SaveChangesAsync();

        var first = await indexer.BuildFullAsync("BTCUSDT", "1h", 100);
        Assert.True(first > 0);

        var afterFirst = await db.CandlePatterns.CountAsync();

        // Thêm 2 nến mới, chạy lại -> chỉ insert patterns cho nến mới.
        db.Klines.AddRange(CreateKlines("1h", 10, 2));
        await db.SaveChangesAsync();

        var second = await indexer.BuildFullAsync("BTCUSDT", "1h", 100);
        Assert.True(second >= 0);
        Assert.True(second < first);
        Assert.Equal(afterFirst + second, await db.CandlePatterns.CountAsync());
    }

    [Fact]
    public async Task CandleVolumeIndexer_IndexAsync_IndexesVolumeAndSkipsExisting()
    {
        await using var db = CreateInMemoryDb();
        var indexer = new CandleVolumeIndexer(
            db,
            NullLogger<CandleVolumeIndexer>.Instance,
            OptionsFactory.Create(DefaultOptions()));

        var klines = CreateKlineDtos("1h", 0, 30);
        var first = await indexer.IndexAsync("BTCUSDT", "1h", klines);
        Assert.Equal(30, first);

        var newKlines = CreateKlineDtos("1h", 30, 2);
        var second = await indexer.IndexAsync("BTCUSDT", "1h", newKlines);
        Assert.Equal(2, second);
        Assert.Equal(32, await db.CandleVolumeStats.CountAsync());
    }

    [Fact]
    public async Task TechnicalIndicatorIndexer_IndexAsync_IndexesIndicatorsAndSkipsExisting()
    {
        await using var db = CreateInMemoryDb();
        var indexer = new TechnicalIndicatorIndexer(
            db,
            NullLogger<TechnicalIndicatorIndexer>.Instance,
            OptionsFactory.Create(DefaultOptions()));

        db.Klines.AddRange(CreateKlines("1h", 0, 300));
        await db.SaveChangesAsync();

        var first = await indexer.IndexAsync("BTCUSDT", "1h");
        Assert.True(first > 0);

        var afterFirst = await db.TechnicalIndicators.CountAsync();

        db.Klines.AddRange(CreateKlines("1h", 300, 2));
        await db.SaveChangesAsync();

        var second = await indexer.IndexAsync("BTCUSDT", "1h");
        Assert.True(second >= 0);
        Assert.True(second < first);
        Assert.Equal(afterFirst + second, await db.TechnicalIndicators.CountAsync());
    }

    [Fact]
    public async Task WindowVectorIndexer_BuildAllForTimeframeAsync_IndexesAllCombinations()
    {
        await using var db = CreateInMemoryDb();
        var indexer = new WindowVectorIndexer(db, OptionsFactory.Create(DefaultOptions()));

        db.Klines.AddRange(CreateKlines("1h", 0, 50));
        await db.SaveChangesAsync();

        var klines = CreateKlineDtos("1h", 0, 50);
        var count = await indexer.BuildAllForTimeframeAsync(
            "BTCUSDT", "1h", klines,
            new[] { "close", "returns_shape" }, new[] { 10, 15 }, CancellationToken.None);

        Assert.True(count > 0);
        Assert.Equal(count, await db.WindowVectors.CountAsync());

        // Chạy lại phải skip existing
        var count2 = await indexer.BuildAllForTimeframeAsync(
            "BTCUSDT", "1h", klines,
            new[] { "close", "returns_shape" }, new[] { 10, 15 }, CancellationToken.None);
        Assert.Equal(0, count2);
    }

    [Fact]
    public async Task TechnicalIndicatorIndexer_IndexAsync_WithKlines_IndexesAndSkipsExisting()
    {
        await using var db = CreateInMemoryDb();
        var indexer = new TechnicalIndicatorIndexer(
            db,
            NullLogger<TechnicalIndicatorIndexer>.Instance,
            OptionsFactory.Create(DefaultOptions()));

        db.Klines.AddRange(CreateKlines("1h", 0, 300));
        await db.SaveChangesAsync();

        var klines = CreateKlineDtos("1h", 0, 300);
        var first = await indexer.IndexAsync("BTCUSDT", "1h", klines);
        Assert.True(first > 0);

        var afterFirst = await db.TechnicalIndicators.CountAsync();

        db.Klines.AddRange(CreateKlines("1h", 300, 2));
        await db.SaveChangesAsync();

        var newKlines = CreateKlineDtos("1h", 0, 302);
        var second = await indexer.IndexAsync("BTCUSDT", "1h", newKlines);
        Assert.True(second >= 0);
        Assert.True(second < first);
        Assert.Equal(afterFirst + second, await db.TechnicalIndicators.CountAsync());
    }

    [Fact]
    public void SliceView_IndexesCorrectRange()
    {
        var source = new[] { 10, 20, 30, 40, 50 };
        var slice = new SliceView<int>(source, 1, 3);

        Assert.Equal(3, slice.Count);
        Assert.Equal(20, slice[0]);
        Assert.Equal(30, slice[1]);
        Assert.Equal(40, slice[2]);
        Assert.Equal(new[] { 20, 30, 40 }, slice.ToArray());
    }

    private static List<Kline> CreateKlines(string timeframe, int startIndex, int count)
    {
        var list = new List<Kline>();
        var baseTime = 1_000_000L;
        var intervalMs = timeframe switch
        {
            "1h" => 3_600_000L,
            _ => 3_600_000L
        };

        var random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            var open = 64000m + random.Next(-500, 500);
            var close = open + random.Next(-200, 200);
            var high = Math.Max(open, close) + random.Next(0, 100);
            var low = Math.Min(open, close) - random.Next(0, 100);
            var t = baseTime + (startIndex + i) * intervalMs;
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

    private static List<KlineDto> CreateKlineDtos(string timeframe, int startIndex, int count)
    {
        return CreateKlines(timeframe, startIndex, count).Select(KlineMapper.ToDto).ToList();
    }
}
