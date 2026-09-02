using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public class EnsemblePaperTraderServiceTests
{
    [Fact]
    public async Task ExistingTradeGetsRiskBoundsWithoutSameBarExit()
    {
        await using var db = CreateDb();
        db.PaperTrades.Add(new PaperTrade
        {
            Symbol = "BTCUSDT", Timeframe = "1h", WindowEndMs = 10_000, EntryTimeMs = 10_000,
            Side = "LONG", Status = "open", EntryPrice = 100, PositionSizeUsdt = 2_000,
            ModelVersion = "Ensemble-5Layer", CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, Kline(10_000, 100, 101, 99));

        var result = await service.EvaluateAndTradeAsync("btcusdt", "1H");

        Assert.Equal("HOLD", result.ActionTaken);
        var trade = await db.PaperTrades.SingleAsync();
        Assert.Equal("open", trade.Status);
        Assert.Equal(101.5, trade.TakeProfitPrice!.Value, 8);
        Assert.Equal(99, trade.StopLossPrice!.Value, 8);
    }

    [Fact]
    public async Task ClosesOnlyMatchingTimeframeAfterMaximumHold()
    {
        await using var db = CreateDb();
        const long now = 7 * 3_600_000;
        db.PaperTrades.AddRange(
            new PaperTrade
            {
                Symbol = "BTCUSDT", Timeframe = "1h", WindowEndMs = 0, EntryTimeMs = 0,
                Side = "long", Status = "OPEN", EntryPrice = 100, PositionSizeUsdt = 2_000,
                ModelVersion = "Ensemble-5Layer", CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
            },
            new PaperTrade
            {
                Symbol = "BTCUSDT", Timeframe = "4h", WindowEndMs = 0, EntryTimeMs = 0,
                Side = "LONG", Status = "open", EntryPrice = 100, PositionSizeUsdt = 2_000,
                ModelVersion = "Ensemble-5Layer", CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
            });
        await db.SaveChangesAsync();
        var service = CreateService(db, Kline(now, 100.5m, 100.8m, 100.2m));

        var result = await service.EvaluateAndTradeAsync("BTCUSDT", "1h");

        Assert.Equal("CLOSED_POSITION", result.ActionTaken);
        var oneHour = await db.PaperTrades.SingleAsync(x => x.Timeframe == "1h");
        var fourHour = await db.PaperTrades.SingleAsync(x => x.Timeframe == "4h");
        Assert.Equal("closed", oneHour.Status);
        Assert.Equal("TIMEOUT", oneHour.ExitReason);
        Assert.NotNull(oneHour.TakeProfitPrice);
        Assert.NotNull(oneHour.StopLossPrice);
        Assert.Equal("open", fourHour.Status);
    }

    [Fact]
    public async Task MissingMarketPriceStopsEvaluationInsteadOfInventingPrice()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EvaluateAndTradeAsync());
        Assert.Empty(await db.PaperTrades.ToListAsync());
    }

    [Fact]
    public async Task UnpromotedEnsembleCannotOpenPaperTrade()
    {
        await using var db = CreateDb();
        var prediction = ValidPrediction(10_000, "Bullish", 0.9);
        db.EnsemblePredictionRecords.Add(prediction);
        await db.SaveChangesAsync();
        var service = CreateService(db, Kline(10_000, 100, 101, 99));

        var result = await service.EvaluateAndTradeAsync();

        Assert.Equal("HOLD", result.ActionTaken);
        Assert.Contains("Experimental", result.SummaryText);
        Assert.Empty(await db.PaperTrades.ToListAsync());
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EnsemblePaperTraderService CreateService(AppDbContext db, params KlineDto[] klines) =>
        new(db, new StubKlinesService(klines));

    private static EnsemblePredictionRecord ValidPrediction(long timeMs, string direction, double confidence)
    {
        var remainder = (1 - confidence) / 2;
        return new EnsemblePredictionRecord
        {
            Symbol = "BTCUSDT", Timeframe = "1h", TimeMs = timeMs, EntryPrice = 100,
            FinalDirection = direction, EnsembleConfidence = confidence,
            ProbUp = direction == "Bullish" ? confidence : remainder,
            ProbDown = direction == "Bearish" ? confidence : remainder,
            ProbSideways = remainder, ValidityStatus = ValidityStatuses.Valid,
            PipelineVersion = ResearchVersions.DataPipeline, EvaluationVersion = ResearchVersions.Evaluation,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static KlineDto Kline(long openTimeMs, decimal close, decimal high, decimal low) => new()
    {
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 3_599_999,
        Open = close,
        High = high,
        Low = low,
        Close = close
    };

    private sealed class StubKlinesService(IReadOnlyList<KlineDto> klines) : IBinanceKlinesService
    {
        public Task<IReadOnlyList<KlineDto>> GetKlinesAsync(string symbol = "BTCUSDT", string interval = "1h", int limit = 48, long? startTimeMs = null, long? endTimeMs = null, CancellationToken cancellationToken = default) => Task.FromResult(klines);
        public Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(string interval = "1h", int limit = 48, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> BuildTechSummaryAsync(string symbol = "BTCUSDT", string interval = "1h", int limit = 48, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MarketTickerDto>> Get24hTickersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MarketTradeDto>> GetRecentTradesAsync(string symbol = "BTCUSDT", int limit = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OrderBookDepthDto> GetOrderBookDepthAsync(string symbol = "BTCUSDT", int limit = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
