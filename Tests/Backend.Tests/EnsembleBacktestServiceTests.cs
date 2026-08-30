using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public class EnsembleBacktestServiceTests
{
    [Fact]
    public async Task RunAsync_UsesPredictionOnlyFromFollowingBar()
    {
        await using var db = CreateDb();
        db.Klines.AddRange(
            Kline(0, 100, 100),
            Kline(3_600_000, 105, 106),
            Kline(7_200_000, 110, 110));
        db.EnsemblePredictionRecords.Add(Prediction(0, "Bullish", 0.8));
        await db.SaveChangesAsync();

        var (run, trades, _) = await new EnsembleBacktestService(db)
            .RunEnsembleBacktestAsync(feeBps: 10);

        var trade = Assert.Single(trades);
        Assert.Equal(3_600_000, trade.EntryTimeMs);
        Assert.Equal(105m, trade.EntryPrice);
        Assert.Equal(110m, trade.ExitPrice);
        Assert.Equal(((110.0 - 105.0) / 105.0) - 0.002, trade.NetReturn, 8);
        Assert.Equal(trade.NetReturn * 100, trade.PnlPct, 8);
        Assert.Equal(1, run.TotalTrades);
        Assert.Equal(1, run.WinRate);
        Assert.Equal(10, run.FeeBps);
    }

    [Fact]
    public async Task RunAsync_ChangesDirectionWithoutUsingFuturePrediction()
    {
        await using var db = CreateDb();
        db.Klines.AddRange(
            Kline(0, 100, 100),
            Kline(3_600_000, 100, 100),
            Kline(7_200_000, 110, 110),
            Kline(10_800_000, 90, 90),
            Kline(14_400_000, 80, 80));
        db.EnsemblePredictionRecords.AddRange(
            Prediction(0, "Bullish", 0.8),
            Prediction(7_200_000, "Bearish", 0.9));
        await db.SaveChangesAsync();

        var (run, trades, curve) = await new EnsembleBacktestService(db)
            .RunEnsembleBacktestAsync(feeBps: 0);

        Assert.Equal(2, trades.Count);
        Assert.Equal("LONG", trades[0].Side);
        Assert.Equal(10_800_000, trades[0].ExitTimeMs);
        Assert.Equal("SHORT", trades[1].Side);
        Assert.Equal(10_800_000, trades[1].EntryTimeMs);
        Assert.Equal(0.5, run.WinRate);
        Assert.Equal(5, curve.Count);
    }

    [Fact]
    public async Task RunAsync_RejectsMissingPointInTimePredictions()
    {
        await using var db = CreateDb();
        db.Klines.AddRange(Kline(0, 100, 100), Kline(3_600_000, 101, 101));
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EnsembleBacktestService(db).RunEnsembleBacktestAsync());

        Assert.Contains("INSUFFICIENT_POINT_IN_TIME_DATA", error.Message);
        Assert.Empty(db.BacktestRuns);
    }

    [Fact]
    public async Task RunAsync_RejectsCustomWeightsWithoutHistoricalLayerScores()
    {
        await using var db = CreateDb();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EnsembleBacktestService(db).RunEnsembleBacktestAsync(
                customWeights: new Dictionary<string, double> { ["confluence"] = 1 }));

        Assert.Contains("INSUFFICIENT_POINT_IN_TIME_LAYER_DATA", error.Message);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Kline Kline(long openTimeMs, decimal open, decimal close) => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "1h",
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 3_599_999,
        Open = open,
        High = Math.Max(open, close),
        Low = Math.Min(open, close),
        Close = close,
        Volume = 1
    };

    private static EnsemblePredictionRecord Prediction(long timeMs, string direction, double confidence) => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "1h",
        TimeMs = timeMs,
        EntryPrice = 100,
        FinalDirection = direction,
        EnsembleConfidence = confidence
    };
}
