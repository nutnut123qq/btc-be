using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public class VolumeProfileServiceTests
{
    [Fact]
    public async Task GetVolumeProfileAsync_ReturnsPricedBinsInsteadOfZeroPlaceholders()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        db.Klines.AddRange(
            Kline(0, 100, 110, 95, 105, 10),
            Kline(3_600_000, 105, 120, 100, 115, 20));
        await db.SaveChangesAsync();

        var snapshot = await new VolumeProfileService(db)
            .GetVolumeProfileAsync("BTCUSDT", "1h", 200);

        Assert.NotNull(snapshot);
        var bins = JsonSerializer.Deserialize<VolumeProfileBinDto[]>(snapshot.ProfileBinsJson);
        Assert.NotNull(bins);
        Assert.Equal(30, bins.Length);
        Assert.All(bins, bin => Assert.True(bin.PriceLevel > 0));
        Assert.Single(bins.Where(bin => bin.IsPoc));
        Assert.Contains(bins, bin => bin.IsValueArea);
    }

    private static Kline Kline(
        long openTimeMs,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume) => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = "1h",
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 3_599_999,
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = volume
    };
}
