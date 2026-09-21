using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.Tests;

public class RegimeDetectionServiceTests
{
    [Fact]
    public async Task BuildRegimes_RetryIsIdempotentAndDurationsMeasureCompletedRegime()
    {
        await using var db = CreateDb();
        var start = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        decimal previous = 20_000;
        for (var i = 0; i < 360; i++)
        {
            var close = i < 260 ? previous + 500 : previous;
            db.Klines.Add(new Kline
            {
                Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = start + i * 3_600_000L,
                CloseTimeMs = start + (i + 1) * 3_600_000L - 1,
                Open = previous, High = Math.Max(previous, close) + 5, Low = Math.Min(previous, close) - 5,
                Close = close, Volume = 100
            });
            previous = close;
        }
        await db.SaveChangesAsync();
        var service = new RegimeDetectionService(db);

        await service.BuildRegimesAsync("BTCUSDT", "1h", 360);
        var first = await db.RegimeTransitions.AsNoTracking().OrderBy(x => x.TransitionTimeMs).ToListAsync();
        await service.BuildRegimesAsync("BTCUSDT", "1h", 360);
        var second = await db.RegimeTransitions.AsNoTracking().OrderBy(x => x.TransitionTimeMs).ToListAsync();

        Assert.NotEmpty(first);
        Assert.All(first, x => Assert.True(x.DurationBars > 0));
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(second.Count, second.Select(x => (x.Symbol, x.Timeframe, x.TransitionTimeMs)).Distinct().Count());
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new AppDbContext(options);
    }
}
