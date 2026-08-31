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
    public async Task EvaluatePredictions_ReevaluatesLegacyResultsWithPointInTimeCandle()
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
            EvaluationStatus = "T",
            ActualPrice24h = 200,
            ActualReturnPct = 100
        });
        db.Klines.Add(Candle(horizonTime, 90));
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

    [Fact]
    public async Task LegacyReevaluation_IsImmutableVersionedAndIdempotent()
    {
        await using var db = CreateDb();
        var predictionTime = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        var horizonTime = predictionTime + 24 * 60 * 60 * 1000L;
        var source = new EnsemblePredictionRecord
        {
            Symbol = "BTCUSDT", Timeframe = "1h", TimeMs = predictionTime,
            EntryPrice = 100, FinalDirection = "Bullish", EnsembleConfidence = 0.8,
            ProbUp = 0.7, ProbDown = 0.2, ProbSideways = 0.1,
            EvaluationStatus = "T", ActualPrice24h = 200, ActualReturnPct = 100, EvaluatedAtMs = horizonTime + 1,
            PipelineVersion = ResearchVersions.Legacy, EvaluationVersion = ResearchVersions.Legacy,
            ValidityStatus = ValidityStatuses.Legacy
        };
        db.EnsemblePredictionRecords.Add(source);
        db.Klines.Add(Candle(horizonTime, 90));
        await db.SaveChangesAsync();

        var service = new EnsembleService(db, new FakeBinanceKlinesService());
        await service.EvaluatePredictionsAsync("BTCUSDT", includeLegacy: true);
        var second = await service.EvaluatePredictionsAsync("BTCUSDT", includeLegacy: true);

        await db.Entry(source).ReloadAsync();
        Assert.Equal("T", source.EvaluationStatus);
        Assert.Equal(200, source.ActualPrice24h);
        var child = Assert.Single(await db.EnsemblePredictionRecords.Where(x => x.SourcePredictionId == source.Id).ToListAsync());
        Assert.Equal(ResearchVersions.Evaluation, child.EvaluationVersion);
        Assert.Equal(ValidityStatuses.Legacy, child.ValidityStatus);
        Assert.Equal("F", child.EvaluationStatus);
        Assert.Equal(90, child.ActualPrice24h);
        Assert.Equal(2, await db.EnsemblePredictionRecords.CountAsync());
        Assert.Equal(1, second.TotalPredictions);
        Assert.Equal(1, second.CanonicalEvaluatedCount);
        Assert.Equal(1, second.ReevaluatedCount);
        Assert.Equal(100, second.WinRatePct);
        Assert.Equal(0, second.ReevaluatedWinRatePct);
    }

    [Fact]
    public void Model_EnforcesOneEvaluationVersionPerSourcePrediction()
    {
        using var db = CreateDb();
        var index = db.Model.FindEntityType(typeof(EnsemblePredictionRecord))!.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "SourcePredictionId", "EvaluationVersion" }));
        Assert.True(index.IsUnique);
        Assert.Equal("\"SourcePredictionId\" IS NOT NULL", index.GetFilter());
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
