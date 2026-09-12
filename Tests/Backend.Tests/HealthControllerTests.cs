using Backend.Controllers;
using Backend.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class HealthControllerTests
{
    [Fact]
    public void Live_ReturnsHealthyWithoutDatabaseQuery()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new AppDbContext(options);
        var controller = new HealthController(db, NullLogger<HealthController>.Instance);

        Assert.IsType<OkObjectResult>(controller.Live());
    }

    [Fact]
    public async Task Ready_ReturnsOkWhenDatabaseIsReachable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var controller = new HealthController(db, NullLogger<HealthController>.Instance);

        Assert.IsType<OkObjectResult>(await controller.Ready(default));
    }

    [Fact]
    public async Task Get_ReportsFreshAndMissingTimeframes()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        db.Klines.AddRange(new[] { "1m", "5m", "15m", "30m", "1h", "4h", "1d" }
            .Select(timeframe => new Kline
            {
                Symbol = "BTCUSDT",
                Timeframe = timeframe,
                OpenTimeMs = nowMs,
                CloseTimeMs = nowMs,
                Open = 1,
                High = 1,
                Low = 1,
                Close = 1
            }));
        await db.SaveChangesAsync();

        var controller = new HealthController(db, NullLogger<HealthController>.Instance);
        var healthy = Assert.IsType<OkObjectResult>((await controller.Get(default)).Result);
        var healthyBody = Assert.IsType<HealthResponse>(healthy.Value);

        Assert.True(healthyBody.DatabaseReachable);
        Assert.Equal("healthy", healthyBody.Status);
        Assert.All(healthyBody.Klines.Where(x => x.Active), item => Assert.Equal("fresh", item.Status));
        Assert.All(healthyBody.Klines.Where(x => !x.Active), item => Assert.Equal("inactive", item.Status));

        db.Klines.RemoveRange(db.Klines.Where(k => k.Timeframe == "1m"));
        await db.SaveChangesAsync();

        var degraded = Assert.IsType<OkObjectResult>((await controller.Get(default)).Result);
        var degradedBody = Assert.IsType<HealthResponse>(degraded.Value);

        Assert.Equal("healthy", degradedBody.Status);
        Assert.Equal("inactive", degradedBody.Klines.Single(x => x.Timeframe == "1m").Status);

        db.Klines.RemoveRange(db.Klines.Where(k => k.Timeframe == "4h"));
        await db.SaveChangesAsync();
        var activeMissing = Assert.IsType<OkObjectResult>((await controller.Get(default)).Result);
        var activeMissingBody = Assert.IsType<HealthResponse>(activeMissing.Value);
        Assert.Equal("degraded", activeMissingBody.Status);
        Assert.Equal("missing", activeMissingBody.Klines.Single(x => x.Timeframe == "4h").Status);
    }
}
