using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;

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
        var result = await service.AuditAsync("BTCUSDT", includeInventory: true);

        var tf = result.Timeframes.Single(t => t.Timeframe == "1h");
        Assert.Equal(3, tf.TotalKlines);
        Assert.Equal(1, tf.CandlePatterns);
        Assert.Equal(1, tf.TechnicalIndicators);
        Assert.Equal(1, tf.WindowVectors);
        Assert.Equal(0, tf.MissingBars);
        Assert.True(tf.Active);

        var inactive = result.Timeframes.Single(t => t.Timeframe == "15m");
        Assert.False(inactive.Active);
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

        var tf = result.Timeframes.Single(t => t.Timeframe == "1h");
        Assert.Equal(3, tf.TotalKlines);
        Assert.Equal(4, tf.ExpectedBars);
        Assert.Equal(1, tf.MissingBars);
        Assert.Equal(1, tf.GapRangeCount);
        Assert.Single(tf.TopGaps);

        var gap = tf.TopGaps[0];
        Assert.Equal(7_200_000L, gap.StartOpenTimeMs);
        Assert.Equal(7_200_000L, gap.EndOpenTimeMs);
        Assert.Equal(1, gap.MissingBars);
        Assert.Equal(3_600_000L, tf.LargestGapMs);
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
            Assert.Equal(0, tf.TotalKlines);
            Assert.Null(tf.CandlePatterns);
            Assert.Null(tf.TechnicalIndicators);
            Assert.Null(tf.WindowVectors);
            Assert.Equal(0, tf.MissingBars);
            Assert.Empty(tf.TopGaps);
            Assert.Null(tf.MinOpenTimeMs);
            Assert.Null(tf.MaxOpenTimeMs);
            Assert.Null(tf.ExpectedBars);
        }

        Assert.NotNull(result.Derivatives);
        Assert.Equal(0, result.Derivatives!.FuturesMetrics.Rows);
    }

    [Fact]
    public async Task AuditAsync_ReportsInvalidFormingDerivedAndDerivativeMissingness()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);
        var finalized = CreateKline("1h", 0);
        var invalid = CreateKline("1h", 3_600_000);
        invalid.High = invalid.Low - 1;
        var forming = CreateKline("1h", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        forming.CloseTimeMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        db.Klines.AddRange(finalized, invalid, forming);
        db.TechnicalIndicators.Add(new TechnicalIndicator
        {
            Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = 0, Rsi14 = 50
        });
        db.FuturesMetrics.Add(new FuturesMetric
        {
            Symbol = "BTCUSDT", OpenTimeMs = 1_000, FundingRate = 0.0001
        });
        db.MarketMetrics.Add(new MarketMetrics
        {
            Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = 1_000,
            FundingRate = 0.0001
        });
        await db.SaveChangesAsync();

        var result = await CreateService(db).AuditAsync("BTCUSDT", includeInventory: true);
        var oneHour = result.Timeframes.Single(x => x.Timeframe == "1h");

        Assert.NotNull(oneHour.Quality);
        Assert.Equal(2, oneHour.Quality!.FinalizedRows);
        Assert.Equal(1, oneHour.Quality.FormingRows);
        Assert.Equal(1, oneHour.Quality.InvalidOhlcvRows);
        Assert.Equal(0, oneHour.Quality.DuplicateOpenTimeRows);
        Assert.Contains(oneHour.DerivedTables!, x =>
            x.Table == "TechnicalIndicators" && x.Rows == 1 && x.MissingRows == 1);

        Assert.NotNull(result.Derivatives);
        Assert.Equal(1, result.Derivatives!.FuturesMetrics.Rows);
        Assert.Equal(1, result.Derivatives.FuturesMetrics.MissingOpenInterest);
        Assert.Equal(1, result.Derivatives.FuturesMetrics.MissingMarkPrice);
        Assert.NotNull(result.Derivatives.FuturesMetrics.Lineage);
        Assert.Equal(0, result.Derivatives.FuturesMetrics.Lineage!.CompleteRows);
        Assert.Equal(1, result.Derivatives.FuturesMetrics.Lineage.MissingReceivedAt);
        Assert.Equal(1, result.Derivatives.FuturesMetrics.Lineage.MissingAvailableAt);
        Assert.Equal(1, result.Derivatives.FuturesMetrics.Lineage.ReconstructedRows);
        var market = Assert.Single(result.Derivatives.MarketMetrics);
        Assert.Equal(1, market.MissingOpenInterest);
        Assert.Equal(1, market.MissingLiquidations);
        Assert.NotNull(market.Lineage);
        Assert.Equal(1, market.Lineage!.ReconstructedRows);
        Assert.Equal(0, market.Lineage.AsOfEligibleRows);
        Assert.Contains("reconstructed", result.Derivatives.AvailabilityCaveat);
    }

    [Fact]
    public async Task AuditAsync_CachesForFiveMinutesAndCanBeInvalidated()
    {
        await using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DataAuditService(db, NullLogger<DataAuditService>.Instance, new DataAuditCache(cache));

        var first = await service.AuditAsync("BTCUSDT");
        db.Klines.Add(CreateKline("1h", 0));
        await db.SaveChangesAsync();
        var cached = await service.AuditAsync("BTCUSDT");
        Assert.Same(first, cached);
        Assert.Equal(0, cached.Timeframes.Single(x => x.Timeframe == "1h").TotalKlines);

        var inventoryVariant = await service.AuditAsync("BTCUSDT", includeInventory: true);
        Assert.NotSame(first, inventoryVariant);
        Assert.Equal(1, inventoryVariant.Timeframes.Single(x => x.Timeframe == "1h").TotalKlines);
        Assert.Equal(0, inventoryVariant.Timeframes.Single(x => x.Timeframe == "1h").CandlePatterns);

        service.Invalidate("BTCUSDT");
        var refreshed = await service.AuditAsync("BTCUSDT");
        Assert.Equal(1, refreshed.Timeframes.Single(x => x.Timeframe == "1h").TotalKlines);
        var refreshedInventory = await service.AuditAsync("BTCUSDT", includeInventory: true);
        Assert.NotSame(inventoryVariant, refreshedInventory);
    }

    [Fact]
    public void CalculateExpectedRange_EmptyTimeframeReportsEntireConfiguredRangeMissing()
    {
        var result = DataAuditService.CalculateExpectedRange(0, 0, 10_800_000, 3_600_000);

        Assert.Equal(4, result.ExpectedBars);
        Assert.Equal(4, result.MissingBars);
    }

    [Fact]
    public void ShouldUseLiveFallback_PartialLedgerCannotValidateEmptyTimeframe()
    {
        Assert.True(DataAuditService.ShouldUseLiveFallback(
            ledgerInitialized: true, minOpenTimeMs: null, maxOpenTimeMs: null, overlapsLatest: false));
        Assert.False(DataAuditService.ShouldUseLiveFallback(
            ledgerInitialized: true, minOpenTimeMs: 0, maxOpenTimeMs: 10, overlapsLatest: false));
    }

    [Fact]
    public void CanExtendTrailingGap_OnlyAllowsEvidenceFreePendingBootstrapTail()
    {
        var bootstrap = new KlineGapState
        {
            Status = KlineGapStatuses.Pending,
            AttemptCount = 0,
            Reason = "BOOTSTRAP_DISCOVERY"
        };

        Assert.True(DataAuditService.CanExtendTrailingGap(bootstrap));
        Assert.False(DataAuditService.CanExtendTrailingGap(new KlineGapState
        {
            Status = KlineGapStatuses.Pending,
            AttemptCount = 1,
            NextRetryAtUtc = DateTime.UtcNow.AddHours(24),
            Reason = "BOOTSTRAP_DISCOVERY"
        }));
        Assert.False(DataAuditService.CanExtendTrailingGap(new KlineGapState
        {
            Status = KlineGapStatuses.Unavailable,
            Reason = "BOOTSTRAP_DISCOVERY"
        }));
    }

    [Fact]
    public void CalculateTrailingExtension_StartsAfterEvidenceBearingTailWithoutOverlap()
    {
        var extension = DataAuditService.CalculateTrailingExtension(100, 160, 10);

        Assert.NotNull(extension);
        Assert.Equal(110, extension.Value.StartOpenTimeMs);
        Assert.Equal(6, extension.Value.MissingBars);
        Assert.True(extension.Value.StartOpenTimeMs > 100);
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
