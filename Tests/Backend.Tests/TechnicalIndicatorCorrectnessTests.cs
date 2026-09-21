using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Backend.Tests;

public class TechnicalIndicatorCorrectnessTests
{
    [Fact]
    public void Rsi14_UsesWilderSeed_GoldenFixture()
    {
        decimal[] closes =
        [
            44.34m, 44.09m, 44.15m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m,
            45.84m, 46.08m, 45.89m, 46.03m, 45.61m, 46.28m, 46.28m, 46.00m
        ];

        var rsi = TechnicalIndicatorIndexer.ComputeRsi(closes, 14);

        Assert.All(rsi.Take(14), x => Assert.Null(x));
        Assert.Equal(70.4641, rsi[14]!.Value, 4);
        Assert.Equal(66.2496, rsi[15]!.Value, 4);
    }

    [Fact]
    public void Atr14_SeedsFromExactlyFourteenTrueRanges()
    {
        var bars = Enumerable.Range(0, 15)
            .Select(i => Bar(i, 100m, 101m, 99m, 100m))
            .ToArray();

        var atr = TechnicalIndicatorIndexer.ComputeAtr(bars, 14);

        Assert.All(atr.Take(13), x => Assert.Null(x));
        Assert.Equal(2d, atr[13]);
        Assert.Equal(2d, atr[14]);
    }

    [Fact]
    public void Rsi14_FlatMarketIsNeutralNotOverbought()
    {
        var rsi = TechnicalIndicatorIndexer.ComputeRsi(
            Enumerable.Repeat(100m, 16).ToArray(), 14);

        Assert.Equal(50d, rsi[14]);
        Assert.Equal(50d, rsi[15]);
    }

    [Fact]
    public void Indicators_ResetAtGap_AndUtcVwapDoesNotZeroFill()
    {
        const long hour = 3_600_000;
        var bars = Enumerable.Range(0, 20)
            .Select(i => Bar(i, 100m, 101m, 99m, 100m, i == 0 ? 0m : 1m))
            .Concat(Enumerable.Range(0, 15)
                .Select(i => Bar(25 + i, 110m, 111m, 109m, 110m, 1m)))
            .ToArray();

        var result = TechnicalIndicatorIndexer.CalculateIndicators(bars, hour);

        Assert.Null(result.Vwap[0]);
        Assert.Null(result.Rsi14[20]);
        Assert.Null(result.Atr14[20]);
        Assert.Null(result.Ema12[20]);
        Assert.Equal(110m, result.Ema12[31]);
        Assert.Equal(2d, result.Atr14[33]);
    }

    [Fact]
    public void FutureAppend_DoesNotChangePastIndicatorValues()
    {
        var initial = Enumerable.Range(0, 260).Select(i =>
            Bar(i, 100m + i / 10m, 102m + i / 10m, 99m + i / 10m, 101m + i / 10m)).ToArray();
        var extended = initial.Concat(Enumerable.Range(260, 20).Select(i =>
            Bar(i, 100m + i / 10m, 102m + i / 10m, 99m + i / 10m, 101m + i / 10m))).ToArray();

        var before = TechnicalIndicatorIndexer.CalculateIndicators(initial, 3_600_000);
        var after = TechnicalIndicatorIndexer.CalculateIndicators(extended, 3_600_000);

        Assert.Equal(before.Ema200, after.Ema200.Take(initial.Length));
        Assert.Equal(before.Rsi14, after.Rsi14.Take(initial.Length));
        Assert.Equal(before.Atr14, after.Atr14.Take(initial.Length));
        Assert.Equal(before.Vwap, after.Vwap.Take(initial.Length));
    }

    [Fact]
    public async Task IncrementalAppend_MatchesFullBatchForNewRows()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var config = Microsoft.Extensions.Options.Options.Create(
            new IndexingOptions { TechnicalIndicatorsBatchSize = 100 });
        var indexer = new TechnicalIndicatorIndexer(db, NullLogger<TechnicalIndicatorIndexer>.Instance, config);
        var all = Enumerable.Range(0, 320).Select(i => Dto(i)).ToArray();

        db.Klines.AddRange(all.Take(300).Select(x => ToEntity(x)));
        await db.SaveChangesAsync();
        await indexer.IndexAsync("BTCUSDT", "1h");
        db.Klines.AddRange(all.Skip(300).Select(x => ToEntity(x)));
        await db.SaveChangesAsync();
        await indexer.IndexAsync("BTCUSDT", "1h");

        var expected = TechnicalIndicatorIndexer.CalculateIndicators(
            all.Select(x => new TechnicalIndicatorIndexer.Bar(x.OpenTimeMs, x.Open, x.High, x.Low, x.Close,
                x.Volume, x.QuoteVolume, x.TakerBuyVolume, x.TakerBuyQuoteVolume, x.TradeCount)).ToArray(),
            3_600_000);
        var actual = await db.TechnicalIndicators.OrderBy(x => x.OpenTimeMs).ToArrayAsync();
        Assert.Equal(320, actual.Length);
        for (var i = 300; i < 320; i++)
        {
            Assert.Equal(expected.Ema200[i], actual[i].Ema200);
            Assert.Equal(expected.Rsi14[i], actual[i].Rsi14);
            Assert.Equal(expected.Atr14[i], actual[i].Atr14);
            Assert.Equal(expected.Obv[i], actual[i].Obv);
            Assert.Equal(expected.Vwap[i], actual[i].Vwap);
        }
    }

    [Fact]
    public void WarmupFloor_BoundsEma200SeedResidual()
    {
        var residual = Math.Pow(199d / 201d,
            TechnicalIndicatorIndexer.RequiredWarmupBars - 200);

        Assert.True(residual < 0.00035, $"EMA200 residual was {residual:P6}");
    }

    private static TechnicalIndicatorIndexer.Bar Bar(
        int index, decimal open, decimal high, decimal low, decimal close, decimal volume = 1m) =>
        new(index * 3_600_000L, open, high, low, close, volume, volume * close,
            volume / 2, volume * close / 2, volume > 0 ? 1 : 0);

    private static KlineDto Dto(int index)
    {
        var close = 100m + (decimal)Math.Sin(index / 7d) * 3m + index / 50m;
        return new KlineDto
        {
            OpenTimeMs = index * 3_600_000L,
            CloseTimeMs = (index + 1) * 3_600_000L - 1,
            Open = close - 0.5m,
            High = close + 1m,
            Low = close - 1m,
            Close = close,
            Volume = 10m + index % 5,
            QuoteVolume = (10m + index % 5) * close,
            TradeCount = 10 + index,
            TakerBuyVolume = 5m,
            TakerBuyQuoteVolume = 5m * close
        };
    }

    private static Kline ToEntity(KlineDto x) => new()
    {
        Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = x.OpenTimeMs, CloseTimeMs = x.CloseTimeMs,
        Open = x.Open, High = x.High, Low = x.Low, Close = x.Close, Volume = x.Volume,
        QuoteVolume = x.QuoteVolume, TradeCount = x.TradeCount,
        TakerBuyVolume = x.TakerBuyVolume, TakerBuyQuoteVolume = x.TakerBuyQuoteVolume
    };
}
