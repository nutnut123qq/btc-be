using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public class EnsembleEvaluationTests
{
    [Fact]
    public async Task EvaluatePredictions_UsesCandleAtHorizonInsteadOfLatestPrice()
    {
        await using var db = CreateDb();
        var predictionTime = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        var horizonTime = predictionTime + 24 * 60 * 60 * 1000L;
        db.EnsemblePredictionRecords.Add(new EnsemblePredictionRecord
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            TimeMs = predictionTime,
            EntryPrice = 100,
            FinalDirection = "Bullish",
            EnsembleConfidence = 0.8,
            EvaluationStatus = "N"
        });
        db.Klines.AddRange(
            Candle(horizonTime, 90),
            Candle(horizonTime + 24 * 60 * 60 * 1000L, 200));
        await db.SaveChangesAsync();

        var service = new EnsembleService(db, new FakeBinanceKlinesService());
        var result = await service.EvaluatePredictionsAsync("BTCUSDT");

        var item = Assert.Single(result.Items);
        Assert.Equal("F", item.EvaluationStatus);
        Assert.Equal(90, item.ActualPrice24h);
        Assert.Equal(-10, item.ActualReturnPct);
        Assert.Equal(horizonTime, item.EvaluatedAtMs);
    }

    [Fact]
    public async Task GetSummary_IsReadOnlyAndLimitsReturnedItems()
    {
        await using var db = CreateDb();
        var oldTime = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        for (var index = 0; index < 3; index++)
        {
            db.EnsemblePredictionRecords.Add(new EnsemblePredictionRecord
            {
                Symbol = "BTCUSDT",
                Timeframe = "1h",
                TimeMs = oldTime + index,
                EntryPrice = 100,
                FinalDirection = "Bullish",
                EnsembleConfidence = 0.8,
                EvaluationStatus = "N"
            });
        }
        await db.SaveChangesAsync();

        var service = new EnsembleService(db, new FakeBinanceKlinesService());
        var result = await service.GetPredictionEvaluationSummaryAsync("BTCUSDT", 2);

        Assert.Equal(3, result.TotalPredictions);
        Assert.Equal(3, result.PendingCount);
        Assert.Equal(2, result.Items.Count);
        Assert.All(await db.EnsemblePredictionRecords.ToListAsync(), item => Assert.Equal("N", item.EvaluationStatus));
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Kline Candle(long openTimeMs, decimal open) => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "1h",
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 3_599_999,
        Open = open,
        High = open,
        Low = open,
        Close = open,
        Volume = 1
    };
}
