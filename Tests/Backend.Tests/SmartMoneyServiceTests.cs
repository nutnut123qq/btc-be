using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.Tests;

public class SmartMoneyServiceTests
{
    [Fact]
    public void DetectStructures_UsesCausalAvailabilityAndConsumesBrokenPivotOnce()
    {
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9),
            Bar(1, 9, 11, 8, 10),
            Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11),
            Bar(4, 11, 12, 10, 11),
            Bar(5, 11, 17, 11, 16),
            Bar(6, 16, 18, 12, 17)
        };

        var events = SmartMoneyService.DetectStructures(rows, "BTCUSDT", "1h");
        var swing = Assert.Single(events.Where(x => x.EventType == "SWING_HIGH"));
        Assert.Equal(rows[2].OpenTimeMs, swing.OriginTimeMs);
        Assert.Equal(rows[4].CloseTimeMs, swing.AvailableTimeMs);

        var broken = Assert.Single(events.Where(x => x.EventType is "BOS_BULL" or "CHOCH_BULL"));
        Assert.Equal(rows[5].CloseTimeMs, broken.AvailableTimeMs);
        Assert.Equal(swing.OriginTimeMs, broken.ReferenceTimeMs);
    }

    [Fact]
    public void DetectStructures_FvgIsAvailableAtFinalDefiningBar()
    {
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9),
            Bar(1, 10, 11, 9, 10),
            Bar(2, 12, 13, 11, 12)
        };

        var fvg = Assert.Single(SmartMoneyService.DetectStructures(rows, "BTCUSDT", "1h")
            .Where(x => x.EventType == "FVG_BULL"));
        Assert.Equal(rows[1].OpenTimeMs, fvg.OriginTimeMs);
        Assert.Equal(rows[2].CloseTimeMs, fvg.AvailableTimeMs);
    }

    [Fact]
    public void DetectStructures_SequentialReplayMatchesBatchEventsAvailableAtEachCutoff()
    {
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9), Bar(1, 9, 11, 8, 10), Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11), Bar(4, 11, 12, 10, 11), Bar(5, 11, 17, 11, 16),
            Bar(6, 16, 18, 12, 17), Bar(7, 17, 18, 13, 14)
        };
        var batch = SmartMoneyService.DetectStructures(rows, "BTCUSDT", "1h");

        for (var count = 3; count <= rows.Length; count++)
        {
            var cutoff = rows[count - 1].CloseTimeMs;
            var replayKeys = SmartMoneyService.DetectStructures(rows.Take(count).ToArray(), "BTCUSDT", "1h")
                .Select(EventKey).Order().ToArray();
            var batchKeys = batch.Where(x => x.AvailableTimeMs <= cutoff)
                .Select(EventKey).Order().ToArray();
            Assert.Equal(batchKeys, replayKeys);
        }
    }

    [Fact]
    public async Task GetStructures_RetryDoesNotDuplicateLogicalEvents()
    {
        await using var db = CreateDb();
        db.Klines.AddRange(new[]
        {
            Bar(0, 9, 10, 8, 9), Bar(1, 9, 11, 8, 10), Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11), Bar(4, 11, 12, 10, 11), Bar(5, 11, 17, 11, 16)
        });
        await db.SaveChangesAsync();
        var service = new SmartMoneyService(db);

        await service.GetSmartMoneyStructuresAsync("BTCUSDT", "1h", 100);
        var firstCount = await db.SmartMoneyStructures.CountAsync();
        await service.GetSmartMoneyStructuresAsync("BTCUSDT", "1h", 100);

        Assert.True(firstCount > 0);
        Assert.Equal(firstCount, await db.SmartMoneyStructures.CountAsync());
    }

    private static string EventKey(SmartMoneyStructure x) =>
        $"{x.EventType}|{x.OriginTimeMs}|{x.AvailableTimeMs}|{x.ReferenceTimeMs}";

    private static Kline Bar(int hour, decimal open, decimal high, decimal low, decimal close)
    {
        const long baseTimeMs = 1_700_000_000_000;
        var openTime = baseTimeMs + hour * 3_600_000L;
        return new Kline
        {
            Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = openTime,
            CloseTimeMs = openTime + 3_599_999, Open = open, High = high, Low = low, Close = close,
            Volume = 100
        };
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new AppDbContext(options);
    }
}
