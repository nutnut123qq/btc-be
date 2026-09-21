using System.Net;
using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public sealed class HistoricalAnalogServiceTests
{
    private const long Interval = 3_600_000L;

    [Fact]
    public async Task SearchAsync_UsesOnlyPriorCompleteIndependentWindows()
    {
        await using var db = CreateDb();
        SeedCandles(db, 220);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SearchAsync(new HistoricalAnalogRequest
        {
            Timeframe = "1h",
            WindowSize = 15,
            NeighborCount = 30,
            PageSize = 8,
            LookbackBars = 200
        }, "no-leakage");

        Assert.NotNull(result.Query);
        Assert.Equal(21, result.ExclusionBars);
        Assert.True(result.RawCandidateCount > result.EffectiveSampleCount);
        Assert.Equal(Math.Min(30, result.IndependentCandidateCount), result.EffectiveSampleCount);
        Assert.All(result.Items, item =>
        {
            Assert.True(item.FutureEndTimeMs < result.Query!.StartTimeMs);
            Assert.Equal(15, item.Ohlc.Count);
            Assert.Equal(6, item.FutureOhlc.Count);
            Assert.Equal(new[] { 1, 3, 6 }, item.Outcomes.Select(x => x.BarsAhead));
        });

        var all = await service.SearchAsync(new HistoricalAnalogRequest
        {
            Timeframe = "1h",
            WindowSize = 15,
            NeighborCount = 30,
            PageSize = 50,
            LookbackBars = 200
        }, "exclusion");
        for (var left = 0; left < all.Items.Count; left++)
        for (var right = left + 1; right < all.Items.Count; right++)
        {
            var endGap = Math.Abs(all.Items[left].EndTimeMs - all.Items[right].EndTimeMs);
            Assert.True(endGap >= all.ExclusionBars * all.IntervalMs,
                $"selected analogs overlap: end gap {endGap / all.IntervalMs} bars");
        }
    }

    [Fact]
    public async Task SearchAsync_ComputesCloseToCloseReturnsAndEconomicDirectionExactly()
    {
        await using var db = CreateDb();
        SeedCandles(db, 180);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SearchAsync(new HistoricalAnalogRequest
        {
            Timeframe = "1h",
            WindowSize = 10,
            NeighborCount = 20,
            PageSize = 20,
            LookbackBars = 150,
            RoundTripCostPct = 0.25,
            AtrMultiplier = 0.5
        }, "formula");

        Assert.NotEmpty(result.Items);
        foreach (var item in result.Items)
        {
            Assert.Equal(Math.Max(0.25, 0.5 * item.Atr14Pct), item.ThresholdPct, 10);
            var baseClose = item.Ohlc[^1].Close;
            foreach (var outcome in item.Outcomes)
            {
                var target = item.FutureOhlc[outcome.BarsAhead - 1];
                var expectedReturn = (double)((target.Close - baseClose) / baseClose * 100m);
                var expectedDirection = expectedReturn > item.ThresholdPct ? 1 : expectedReturn < -item.ThresholdPct ? -1 : 0;
                Assert.Equal(target.OpenTimeMs, outcome.TargetOpenTimeMs);
                Assert.Equal(target.Close, outcome.TargetClose);
                Assert.Equal(expectedReturn, outcome.ReturnPct, 10);
                Assert.Equal(item.ThresholdPct, outcome.ThresholdPct, 10);
                Assert.Equal(expectedDirection, outcome.Direction);
            }
        }

        Assert.All(result.Summaries, summary =>
        {
            Assert.Equal(summary.TotalSamples, summary.UpCount + summary.DownCount + summary.NeutralCount);
            Assert.Equal(1.0, summary.UpRate + summary.DownRate + summary.NeutralRate, 10);
        });
    }

    [Fact]
    public async Task SearchAsync_SeparatesShapeFromContextSimilarity()
    {
        await using var db = CreateDb();
        SeedCandles(db, 180);
        await db.SaveChangesAsync();
        foreach (var candle in db.Klines)
        {
            db.MlFeatureStores.Add(new MlFeatureStore
            {
                Symbol = candle.Symbol,
                Timeframe = candle.Timeframe,
                OpenTimeMs = candle.OpenTimeMs,
                Rsi14 = 30 + candle.Id % 40,
                MacdHistogramNorm = 0.1,
                Ema50Dist = 1,
                Sma200Dist = 2,
                BollingerWidth = 4,
                BollingerPosition = 0.5,
                Atr14Pct = 0.8,
                VolumeZscore = 0.2,
                TakerBuyRatio = 0.52
            });
        }
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SearchAsync(new HistoricalAnalogRequest
        {
            Timeframe = "1h", WindowSize = 20, NeighborCount = 10, PageSize = 10, LookbackBars = 150
        }, "context");

        Assert.NotNull(result.Query);
        Assert.True(result.Query!.Context.AvailableFeatureCount >= 4);
        Assert.All(result.Items, item =>
        {
            Assert.InRange(item.ShapeSimilarity, -1, 1);
            Assert.NotNull(item.ContextSimilarity);
            Assert.InRange(item.ContextSimilarity!.Value, 0, 1);
            Assert.True(item.ContextComparableFeatureCount >= 4);
        });
    }

    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    public async Task SearchAsync_RejectsUnsupportedWindowSize(int windowSize)
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            new HistoricalAnalogRequest { WindowSize = windowSize }, "invalid"));
    }

    [Fact]
    public async Task SearchAsync_RejectsUnsupportedSymbol()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            new HistoricalAnalogRequest { Symbol = "DOGEUSDT" }, "invalid-symbol"));
    }

    [Fact]
    public async Task HttpEndpoint_ExposesVersionedReadOnlyContract()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/historical-analogs?symbol=BTCUSDT&timeframe=4h&windowSize=15&neighborCount=50&page=1&pageSize=8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("2026-09-historical-analogs-v2", root.GetProperty("contractVersion").GetString());
        Assert.Equal("historical-analog-returns-shape-v2", root.GetProperty("method").GetString());
        Assert.Equal("returns_shape_v2_signed", root.GetProperty("methodVersion").GetString());
        Assert.Equal("cosine-similarity-desc-point-in-time", root.GetProperty("rankingMethod").GetString());
        Assert.Equal("fixed-horizon-close-to-close-economic-threshold", root.GetProperty("evaluationMethod").GetString());
        Assert.Equal(21, root.GetProperty("exclusionBars").GetInt32());
        Assert.False(root.GetProperty("validation").GetProperty("isOutOfSampleValidated").GetBoolean());
        Assert.True(root.TryGetProperty("rawCandidateCount", out _));
        Assert.True(root.TryGetProperty("independentCandidateCount", out _));
        Assert.True(root.TryGetProperty("effectiveSampleCount", out _));
        Assert.Equal("unavailable", root.GetProperty("evidence").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("baselineScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lift").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("liftConfidenceInterval").ValueKind);
    }

    [Fact]
    public void ReturnsShapeV2_MatchesPythonGoldenVector_AndSeparatesDirection()
    {
        var bullish = DirectionalWindow([1m, 1m, 1m, 1m]);
        var bearish = DirectionalWindow([-1m, -1m, -1m, -1m]);
        var mixed = DirectionalWindow([1m, 1m, -1m, -1m]);

        var bullishVector = HistoricalAnalogV2Representation.BuildVector(bullish, 0, 4)!;
        var bearishVector = HistoricalAnalogV2Representation.BuildVector(bearish, 0, 4)!;
        var mixedVector = HistoricalAnalogV2Representation.BuildVector(mixed, 0, 4)!;
        var expectedBullish = new float[]
        {
            0f, 0.333333343f, 0.166666672f, 0.166666672f,
            0.333333343f, 0.333333343f, 0.166666672f, 0.166666672f,
            0.333333343f, 0.333333343f, 0.166666672f, 0.166666672f,
            0.333333343f, 0.333333343f, 0.166666672f, 0.166666672f
        };

        Assert.Equal(expectedBullish.Length, bullishVector.Length);
        for (var index = 0; index < expectedBullish.Length; index++)
            Assert.Equal(expectedBullish[index], bullishVector[index], 6);

        var bullishSimilarity = Similarity(bullishVector, bullishVector);
        var mixedSimilarity = Similarity(bullishVector, mixedVector);
        var bearishSimilarity = Similarity(bullishVector, bearishVector);
        Assert.Equal(1.000000119, bullishSimilarity, 6);
        Assert.Equal(0.111111119, mixedSimilarity, 6);
        Assert.Equal(-0.555555582, bearishSimilarity, 6);
        Assert.True(bullishSimilarity > mixedSimilarity && mixedSimilarity > bearishSimilarity);
        Assert.True(bearishSimilarity < 0.25, "opposite candle direction must not rank as a close analog");
    }

    [Fact]
    public async Task SearchAsync_FixedAsOf_IsInvariantWhenFutureCandlesAreAppended()
    {
        await using var db = CreateDb();
        SeedCandles(db, 220);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var cutoff = db.Klines.Max(row => row.CloseTimeMs);
        var request = new HistoricalAnalogRequest
        {
            Timeframe = "1h", WindowSize = 15, NeighborCount = 30, PageSize = 50,
            LookbackBars = 200, MinimumMeanSimilarity = -1, AsOfTimeMs = cutoff
        };

        var before = await service.SearchAsync(request, "before-append");
        SeedCandles(db, 20, 220);
        await db.SaveChangesAsync();
        var after = await service.SearchAsync(request, "after-append");

        Assert.Equal(before.Query!.StartTimeMs, after.Query!.StartTimeMs);
        Assert.Equal(before.Query.EndTimeMs, after.Query.EndTimeMs);
        Assert.Equal(before.Query.AvailableAtTimeMs, after.Query.AvailableAtTimeMs);
        Assert.Equal(before.Items.Select(item => item.WindowId), after.Items.Select(item => item.WindowId));
        Assert.Equal(before.Items.Select(item => item.ShapeSimilarity), after.Items.Select(item => item.ShapeSimilarity));
        Assert.Equal(before.Summaries.Select(summary => summary.DominantDirection), after.Summaries.Select(summary => summary.DominantDirection));
    }

    [Fact]
    public async Task SearchAsync_QualityGateAbstainsWithoutFabricatingEvidence()
    {
        await using var db = CreateDb();
        SeedCandles(db, 220);
        await db.SaveChangesAsync();

        var result = await CreateService(db).SearchAsync(new HistoricalAnalogRequest
        {
            Timeframe = "1h", WindowSize = 15, NeighborCount = 30, PageSize = 50,
            LookbackBars = 200, MinimumMeanSimilarity = 1.0
        }, "quality-abstention");

        Assert.True(result.Abstained);
        Assert.False(result.QualityGatePassed);
        Assert.Null(result.Coverage);
        Assert.Null(result.AbstentionRate);
        Assert.NotNull(result.AbstentionReason);
        Assert.Empty(result.Items);
        Assert.Empty(result.Summaries);
        Assert.Null(result.BaselineName);
        Assert.Null(result.BaselineScore);
        Assert.Null(result.Lift);
        Assert.Null(result.LiftConfidenceInterval);
        Assert.Equal("unavailable", result.Evidence.Status);
    }

    [Fact]
    public async Task SearchAsync_ExcludesFormingCandleFromQueryWindow()
    {
        await using var db = CreateDb();
        SeedCandles(db, 220);
        await db.SaveChangesAsync();
        var lastFinalizedOpenTime = db.Klines.Max(row => row.OpenTimeMs);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.Klines.Add(new Kline
        {
            Id = 221,
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = lastFinalizedOpenTime + Interval,
            CloseTimeMs = nowMs + Interval,
            Open = 21_000m,
            High = 21_100m,
            Low = 20_900m,
            Close = 21_050m,
            Volume = 100
        });
        await db.SaveChangesAsync();

        var result = await CreateService(db).SearchAsync(new HistoricalAnalogRequest
        {
            Timeframe = "1h", WindowSize = 15, NeighborCount = 20,
            LookbackBars = 200, MinimumMeanSimilarity = -1, AsOfTimeMs = nowMs
        }, "closed-bars-only");

        Assert.NotNull(result.Query);
        Assert.Equal(lastFinalizedOpenTime, result.Query!.EndTimeMs);
        Assert.Equal("closed", result.SignalBarState);
        Assert.DoesNotContain(result.Query.Ohlc, row => row.OpenTimeMs > lastFinalizedOpenTime);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"historical-analog-{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private static HistoricalAnalogService CreateService(AppDbContext db) =>
        new(db, new ProductionTimeframePolicy(), NullLogger<HistoricalAnalogService>.Instance);

    private static double Similarity(float[] left, float[] right)
    {
        var leftNorm = (float)Math.Sqrt(left.Sum(value => value * value));
        var rightNorm = (float)Math.Sqrt(right.Sum(value => value * value));
        return PatternVectorSimilarity.Cosine(left, leftNorm, right, rightNorm);
    }

    private static List<KlineDto> DirectionalWindow(IReadOnlyList<decimal> moves)
    {
        var rows = new List<KlineDto>();
        var previous = 100m;
        foreach (var move in moves)
        {
            var close = previous + move;
            rows.Add(new KlineDto
            {
                Open = previous,
                High = Math.Max(previous, close) + 0.5m,
                Low = Math.Min(previous, close) - 0.5m,
                Close = close,
                Volume = 1
            });
            previous = close;
        }
        return rows;
    }

    private static void SeedCandles(AppDbContext db, int count, int startIndex = 0)
    {
        for (var offset = 0; offset < count; offset++)
        {
            var index = startIndex + offset;
            var baseline = 20_000m + index * 3m + (decimal)(Math.Sin(index * 0.37) * 120);
            var close = baseline + (decimal)(Math.Sin(index * 0.83) * 45);
            db.Klines.Add(new Kline
            {
                Id = index + 1,
                Symbol = "BTCUSDT",
                Timeframe = "1h",
                OpenTimeMs = 1_000_000L + index * Interval,
                CloseTimeMs = 1_000_000L + (index + 1) * Interval - 1,
                Open = baseline,
                High = Math.Max(baseline, close) + 35m,
                Low = Math.Min(baseline, close) - 35m,
                Close = close,
                Volume = 100m + index
            });
        }
    }
}
