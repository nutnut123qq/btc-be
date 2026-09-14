using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class ArchetypeServiceEvidenceTests
{
    private const long Interval = 14_400_000L;
    private const string Symbol = "BTCUSDT";

    [Fact]
    public async Task GetOccurrences_ReturnsAuditableFixedHorizonResultsAndMissingDataHonestly()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.ArchetypeOccurrences.AddRange(
            Occurrence(1, start: 0, end: 2 * Interval),
            Occurrence(2, start: 10 * Interval, end: 12 * Interval));

        db.Klines.AddRange(
            Bar(1, 0, 10), Bar(2, Interval, 11), Bar(3, 2 * Interval, 12),
            Bar(4, 3 * Interval, 13), Bar(5, 4 * Interval, 12.5m), Bar(6, 5 * Interval, 10),
            Bar(7, 6 * Interval, 11), Bar(8, 7 * Interval, 12), Bar(9, 8 * Interval, 15),
            Bar(10, 10 * Interval, 20), Bar(11, 12 * Interval, 22),
            Bar(12, 3 * Interval, 999, symbol: "ETHUSDT"));
        await db.SaveChangesAsync();

        var service = new ArchetypeService(db, null!, NullLogger<ArchetypeService>.Instance);

        var latest = await service.GetOccurrencesAsync(7, page: 1, pageSize: 1);
        var older = await service.GetOccurrencesAsync(7, page: 2, pageSize: 1);

        Assert.Equal(2, latest.Total);
        Assert.Equal(2, older.Total);

        var latestItem = Assert.Single(latest.Items);
        Assert.Equal(10 * Interval, latestItem.WindowStartMs);
        Assert.False(latestItem.OhlcComplete);
        Assert.False(latestItem.FutureOhlcComplete);
        Assert.All(latestItem.FixedHorizonOutcomes, x => Assert.False(x.Available));

        var olderItem = Assert.Single(older.Items);
        Assert.Equal(new long[] { 0, Interval, 2 * Interval }, olderItem.Ohlc.Select(x => x.OpenTimeMs));
        Assert.True(olderItem.OhlcComplete);
        Assert.True(olderItem.FutureOhlcComplete);
        Assert.Equal(6, olderItem.FutureOhlc.Count);

        var afterOne = olderItem.FixedHorizonOutcomes.Single(x => x.BarsAhead == 1);
        Assert.True(afterOne.Available);
        Assert.Equal(1, afterOne.Direction);
        Assert.Equal(8.333333333333334, afterOne.ReturnPct!.Value, precision: 10);

        var afterThree = olderItem.FixedHorizonOutcomes.Single(x => x.BarsAhead == 3);
        Assert.Equal(-1, afterThree.Direction);
        Assert.Equal(-16.666666666666668, afterThree.ReturnPct!.Value, precision: 10);

        var afterSix = olderItem.FixedHorizonOutcomes.Single(x => x.BarsAhead == 6);
        Assert.Equal(1, afterSix.Direction);
        Assert.Equal(25, afterSix.ReturnPct);

        Assert.Equal(new[] { 1, 3, 6 }, older.Summaries.Select(x => x.BarsAhead));
        Assert.Equal(new int?[] { 1, -1, 1 }, older.Summaries.Select(x => x.DominantDirection));
        Assert.All(older.Summaries, x => Assert.Equal(1, x.TotalSamples));
    }

    private static ArchetypeOccurrence Occurrence(long id, long start, long end) => new()
    {
        Id = id,
        ArchetypeId = 7,
        Symbol = Symbol,
        Timeframe = "4h",
        WindowSize = 3,
        WindowStartMs = start,
        WindowEndMs = end
    };

    private static Kline Bar(long id, long openTimeMs, decimal close, string symbol = Symbol) => new()
    {
        Id = id,
        Symbol = symbol,
        Timeframe = "4h",
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + Interval - 1,
        Open = close - 1,
        High = close + 1,
        Low = close - 2,
        Close = close,
        Volume = 100
    };
}
