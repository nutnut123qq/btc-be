using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public class DerivativeAsOfQueryTests
{
    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    [Fact]
    public async Task LatestFuturesAsync_ExcludesFutureAndLegacyAvailability()
    {
        await using var db = CreateDb();
        db.FuturesMetrics.AddRange(
            Row(eventMs: 100, availableMs: 200, received: true),
            Row(eventMs: 150, availableMs: 400, received: true),
            Row(eventMs: 50, availableMs: null, received: false, reconstructed: true),
            Row(eventMs: 500, availableMs: 250, received: true),
            new FuturesMetric
            {
                Symbol = "BTCUSDT", OpenTimeMs = 175, SourceEventTimeMs = 175,
                AvailableTimeMs = 250, ReceivedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(400),
                Source = "invalid-future-receipt", MarketType = "usd-m-perpetual"
            });
        await db.SaveChangesAsync();

        var selected = await DerivativeAsOfQuery.LatestFuturesAsync(db, "BTCUSDT", 300);

        Assert.NotNull(selected);
        Assert.Equal(100, selected!.SourceEventTimeMs);
        Assert.Equal(200, selected.AvailableTimeMs);
    }

    [Fact]
    public async Task LatestFuturesAsync_IsInvariantWhenFutureObservationIsAppended()
    {
        await using var db = CreateDb();
        db.FuturesMetrics.Add(Row(eventMs: 100, availableMs: 200, received: true));
        await db.SaveChangesAsync();
        var before = await DerivativeAsOfQuery.LatestFuturesAsync(db, "BTCUSDT", 300);

        db.FuturesMetrics.Add(Row(eventMs: 250, availableMs: 301, received: true));
        await db.SaveChangesAsync();
        var after = await DerivativeAsOfQuery.LatestFuturesAsync(db, "BTCUSDT", 300);

        Assert.Equal(before!.Id, after!.Id);
    }

    [Fact]
    public async Task LatestMarketAsync_UsesAvailabilityNotOnlyEventTime()
    {
        await using var db = CreateDb();
        db.MarketMetrics.AddRange(
            MarketRow(eventMs: 100, availableMs: 220),
            MarketRow(eventMs: 200, availableMs: 320));
        await db.SaveChangesAsync();

        var selected = await DerivativeAsOfQuery.LatestMarketAsync(db, "BTCUSDT", "8h", 300);

        Assert.NotNull(selected);
        Assert.Equal(100, selected!.SourceEventTimeMs);
    }

    private static FuturesMetric Row(long eventMs, long? availableMs, bool received, bool reconstructed = false) => new()
    {
        Symbol = "BTCUSDT",
        OpenTimeMs = eventMs,
        SourceEventTimeMs = eventMs,
        ReceivedAtUtc = received ? DateTimeOffset.FromUnixTimeMilliseconds(availableMs!.Value) : null,
        AvailableTimeMs = availableMs,
        Source = received ? "fixture" : "legacy-unknown",
        MarketType = "usd-m-perpetual",
        IsReconstructed = reconstructed
    };

    private static MarketMetrics MarketRow(long eventMs, long availableMs) => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "8h",
        OpenTimeMs = eventMs,
        SourceEventTimeMs = eventMs,
        ReceivedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(availableMs),
        AvailableTimeMs = availableMs,
        Source = "fixture",
        MarketType = "usd-m-perpetual",
        IsReconstructed = false
    };
}
