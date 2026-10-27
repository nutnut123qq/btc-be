using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Tests;

public class RegimeControllerTests
{
    [Fact]
    public async Task GetHistory_LimitIsClampedBeforeCacheKey()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        for (var i = 0; i < 205; i++)
            db.MarketRegimes.Add(new MarketRegime { Symbol = "BTCUSDT", Timeframe = "4h", OpenTimeMs = 1_000_000 + i });
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new RegimeController(new RegimeDetectionService(db), cache);

        Assert.Equal(200, ItemCount(await controller.GetHistory("BTCUSDT", "4h", limit: 500)));
        Assert.Equal(200, ItemCount(await controller.GetHistory("BTCUSDT", "4h", limit: 100_000)));
        Assert.Equal(2, ItemCount(await controller.GetHistory("BTCUSDT", "4h", limit: 2)));
        Assert.Equal(1, ItemCount(await controller.GetHistory("BTCUSDT", "4h", limit: 0)));
        Assert.Equal(1, ItemCount(await controller.GetHistory("BTCUSDT", "4h", limit: -5)));
        // Out-of-range limits share the clamped entry instead of minting one cache entry each.
        Assert.Equal(3, cache.Count);
    }

    private static int ItemCount(IActionResult result) =>
        Assert.IsType<List<MarketRegime>>(Assert.IsType<OkObjectResult>(result).Value).Count;
}
