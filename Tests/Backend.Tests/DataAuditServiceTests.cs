using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class DataAuditServiceTests
{
    private static AppDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static DataAuditService CreateService(AppDbContext db)
    {
        return new DataAuditService(db, NullLogger<DataAuditService>.Instance);
    }

    [Fact]
    public async Task AuditAsync_CountsMatchSeededData()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);

        db.Klines.AddRange(
            CreateKline("1h", 0L),
            CreateKline("1h", 3_600_000L),
            CreateKline("1h", 7_200_000L));
        db.CandlePatterns.Add(new CandlePattern
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = 0L,
            PatternType = "Doji",
            PatternCategory = "Single",
            TrendDirection = "Sideways",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.TechnicalIndicators.Add(new TechnicalIndicator
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = 0L,
            Rsi14 = 50
        });
        db.WindowVectors.Add(new WindowVector
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            FeatureType = "close",
            WindowSize = 10,
            StartTimeMs = 0L,
            EndTimeMs = 3_600_000L,
            Vector = new[] { 0.5f },
            VectorDim = 1,
            VectorNorm = 0.5f,
            Version = 2,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.AuditAsync("BTCUSDT");

        var tf = result.Timeframes["1h"];
        Assert.Equal(3, tf.KlinesCount);
        Assert.Equal(1, tf.CandlePatternsCount);
        Assert.Equal(1, tf.TechnicalIndicatorsCount);
        Assert.Equal(1, tf.WindowVectorsCount);
        Assert.Equal(0, tf.GapCount);
    }

    [Fact]
    public async Task AuditAsync_WithGap_DetectsGapCorrectly()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);

        // Có 3 nến 1h nhưng thiếu nến giữa 1_003_600_000 và 1_010_800_000.
        db.Klines.AddRange(
            CreateKline("1h", 0L),
            CreateKline("1h", 3_600_000L),
            CreateKline("1h", 10_800_000L));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.AuditAsync("BTCUSDT");

        var tf = result.Timeframes["1h"];
        Assert.Equal(3, tf.KlinesCount);
        Assert.Equal(4, tf.ExpectedCount);
        Assert.Equal(1, tf.GapCount);
        Assert.Single(tf.Gaps);

        var gap = tf.Gaps[0];
        Assert.Equal(7_200_000L, gap.StartMs);
        Assert.Equal(7_200_000L, gap.EndMs);
        Assert.Equal(1, gap.MissingCount);
    }

    [Fact]
    public async Task AuditAsync_EmptyDatabase_ReturnsZeroCounts()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);
        var service = CreateService(db);

        var result = await service.AuditAsync("BTCUSDT");

        Assert.Equal("BTCUSDT", result.Symbol);
        Assert.NotEmpty(result.Timeframes);

        foreach (var tf in result.Timeframes)
        {
            Assert.Equal(0, tf.Value.KlinesCount);
            Assert.Equal(0, tf.Value.CandlePatternsCount);
            Assert.Equal(0, tf.Value.TechnicalIndicatorsCount);
            Assert.Equal(0, tf.Value.WindowVectorsCount);
            Assert.Equal(0, tf.Value.GapCount);
            Assert.Empty(tf.Value.Gaps);
            Assert.Null(tf.Value.MinOpenTimeMs);
            Assert.Null(tf.Value.MaxOpenTimeMs);
            Assert.Null(tf.Value.ExpectedCount);
        }
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
}
