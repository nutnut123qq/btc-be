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

    [Fact]
    public async Task Workers_MapsCycleOutcomesAndCounters()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var now = DateTime.UtcNow;
        db.WorkerHeartbeats.AddRange(
            new WorkerHeartbeat
            {
                WorkerName = "EmbeddingBackfillWorker", Status = "Disabled",
                LastStartedAtUtc = now, LastSucceededAtUtc = now, UpdatedAtUtc = now,
                LastCycleAttempted = 0, LastCycleSucceeded = 0, LastCycleFailed = 0,
                LastCycleSkipped = 16440, LastCycleRemaining = 16440,
                LastCycleDetail = "Gemini API key not configured."
            },
            new WorkerHeartbeat
            {
                WorkerName = "KlinesIngestionWorker", Status = "Partial",
                LastStartedAtUtc = now, LastSucceededAtUtc = now, UpdatedAtUtc = now,
                LastCycleAttempted = 50, LastCycleSucceeded = 48, LastCycleFailed = 2,
                LastCycleSkipped = 0, LastCycleRemaining = 3,
                LastCycleDetail = "failures: rate_limit=2"
            },
            new WorkerHeartbeat
            {
                WorkerName = "IndexingBackgroundWorker", Status = "Idle",
                LastStartedAtUtc = now, LastSucceededAtUtc = now, UpdatedAtUtc = now
            },
            new WorkerHeartbeat
            {
                WorkerName = "RssIngestionService", Status = "Succeeded",
                LastStartedAtUtc = now, LastSucceededAtUtc = now, UpdatedAtUtc = now
            },
            new WorkerHeartbeat
            {
                WorkerName = "CausalSmartMoneyRebuildWorker", Status = "Failed",
                LastStartedAtUtc = now, LastFailedAtUtc = now, UpdatedAtUtc = now,
                LastError = "DbUpdateException: boom"
            });
        await db.SaveChangesAsync();
        var controller = new HealthController(db, NullLogger<HealthController>.Instance);

        var ok = Assert.IsType<OkObjectResult>((await controller.Workers(default)).Result);
        var body = Assert.IsType<WorkerHealthResponse>(ok.Value);

        var disabled = body.Workers.Single(w => w.Name == "EmbeddingBackfillWorker");
        Assert.Equal("disabled", disabled.Status);
        Assert.Equal("Gemini API key not configured.", disabled.Message);
        Assert.Equal(16440, disabled.Remaining);

        var partial = body.Workers.Single(w => w.Name == "KlinesIngestionWorker");
        Assert.Equal("partial", partial.Status);
        Assert.Equal(50, partial.Attempted);
        Assert.Equal(48, partial.Succeeded);
        Assert.Equal(2, partial.Failed);
        Assert.Equal(3, partial.Remaining);
        Assert.Equal("failures: rate_limit=2", partial.Message);

        Assert.Equal("idle", body.Workers.Single(w => w.Name == "IndexingBackgroundWorker").Status);
        Assert.Equal("healthy", body.Workers.Single(w => w.Name == "RssIngestionService").Status);

        var failedWorker = body.Workers.Single(w => w.Name == "CausalSmartMoneyRebuildWorker");
        Assert.Equal("failed", failedWorker.Status);
        Assert.Contains("boom", failedWorker.Message);
    }

    [Fact]
    public async Task Workers_StaleWhenLastCycleTooOld()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var old = DateTime.UtcNow.AddHours(-10);
        db.WorkerHeartbeats.Add(new WorkerHeartbeat
        {
            WorkerName = "EmbeddingBackfillWorker", Status = "Disabled",
            LastStartedAtUtc = old, LastSucceededAtUtc = old, UpdatedAtUtc = old
        });
        await db.SaveChangesAsync();
        var controller = new HealthController(db, NullLogger<HealthController>.Instance);

        var ok = Assert.IsType<OkObjectResult>((await controller.Workers(default)).Result);
        var body = Assert.IsType<WorkerHealthResponse>(ok.Value);

        Assert.Equal("stale", body.Workers.Single(w => w.Name == "EmbeddingBackfillWorker").Status);
    }
}
