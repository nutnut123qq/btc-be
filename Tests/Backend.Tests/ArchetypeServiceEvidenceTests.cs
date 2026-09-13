using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class ArchetypeServiceEvidenceTests
{
    [Fact]
    public async Task GetOccurrences_ReturnsExactPersistedOhlcAndTruthfulCompleteness()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.ArchetypeOccurrences.AddRange(
            new ArchetypeOccurrence
            {
                Id = 1,
                ArchetypeId = 7,
                Symbol = "BTCUSDT",
                Timeframe = "4h",
                WindowSize = 3,
                WindowStartMs = 100,
                WindowEndMs = 300,
                Horizon = "4h",
                Label = 1,
                TargetReturn = 0.75
            },
            new ArchetypeOccurrence
            {
                Id = 2,
                ArchetypeId = 7,
                Symbol = "BTCUSDT",
                Timeframe = "4h",
                WindowSize = 3,
                WindowStartMs = 500,
                WindowEndMs = 700,
                Horizon = "4h",
                Label = -1,
                TargetReturn = -0.8
            });

        db.Klines.AddRange(
            Bar(1, 100, 10), Bar(2, 200, 11), Bar(3, 300, 12),
            Bar(4, 500, 20), Bar(5, 700, 22),
            Bar(6, 600, 999, symbol: "ETHUSDT"));
        db.WindowClassificationDatasets.Add(new WindowClassificationDataset
        {
            Id = 10,
            Symbol = "BTCUSDT",
            Timeframe = "4h",
            WindowSize = 3,
            Horizon = "4h",
            WindowStartMs = 100,
            WindowEndMs = 300,
            FeatureVector = [1f],
            FeatureDim = 1,
            Label = 1,
            TargetReturn = 0.75
        });
        await db.SaveChangesAsync();

        var service = new ArchetypeService(db, null!, NullLogger<ArchetypeService>.Instance);

        var (total, latest) = await service.GetOccurrencesAsync(7, "4h", page: 1, pageSize: 1);
        var (secondTotal, older) = await service.GetOccurrencesAsync(7, "4h", page: 2, pageSize: 1);

        Assert.Equal(2, total);
        Assert.Equal(2, secondTotal);

        var latestItem = Assert.Single(latest);
        Assert.Equal(500, latestItem.WindowStartMs);
        Assert.Equal(new long[] { 500, 700 }, latestItem.Ohlc.Select(x => x.OpenTimeMs));
        Assert.False(latestItem.OhlcComplete);
        Assert.False(latestItem.OutcomeAvailable);
        Assert.Null(latestItem.TargetReturn);

        var olderItem = Assert.Single(older);
        Assert.Equal(100, olderItem.WindowStartMs);
        Assert.Equal(new long[] { 100, 200, 300 }, olderItem.Ohlc.Select(x => x.OpenTimeMs));
        Assert.True(olderItem.OhlcComplete);
        Assert.True(olderItem.OutcomeAvailable);
        Assert.Equal(1, olderItem.Label);
        Assert.Equal(0.75, olderItem.TargetReturn);
        Assert.Equal(12m, olderItem.Ohlc[^1].Close);
    }

    private static Kline Bar(long id, long openTimeMs, decimal close, string symbol = "BTCUSDT") => new()
    {
        Id = id,
        Symbol = symbol,
        Timeframe = "4h",
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 1,
        Open = close - 1,
        High = close + 1,
        Low = close - 2,
        Close = close,
        Volume = 100
    };
}
