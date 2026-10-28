using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    public async Task Get_ToleratesOneFullyMissedClosePerTimeframe()
    {
        // Threshold = 3 candle periods: right after a close, the just-closed
        // candle being absent (normal ingest lag) leaves age in [2P, 3P) — that
        // must stay "fresh". Under the old 2P thresholds this flipped stale at
        // every close and drove the watchdog restart loop.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var now = DateTimeOffset.UtcNow;
        // Newest stored open = the close BEFORE the last one: last closed candle
        // still in transit. age lands in [2P, 3P) regardless of boundary timing.
        var hourOpen = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var fourHourOpen = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.FromHours(4).Ticks, TimeSpan.Zero);
        var dayOpen = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        db.Klines.AddRange(
            new Kline { Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = hourOpen.AddHours(-2).ToUnixTimeMilliseconds(), CloseTimeMs = hourOpen.AddHours(-1).ToUnixTimeMilliseconds() - 1, Open = 1, High = 1, Low = 1, Close = 1 },
            new Kline { Symbol = "BTCUSDT", Timeframe = "4h", OpenTimeMs = fourHourOpen.AddHours(-8).ToUnixTimeMilliseconds(), CloseTimeMs = fourHourOpen.AddHours(-4).ToUnixTimeMilliseconds() - 1, Open = 1, High = 1, Low = 1, Close = 1 },
            new Kline { Symbol = "BTCUSDT", Timeframe = "1d", OpenTimeMs = dayOpen.AddDays(-2).ToUnixTimeMilliseconds(), CloseTimeMs = dayOpen.AddDays(-1).ToUnixTimeMilliseconds() - 1, Open = 1, High = 1, Low = 1, Close = 1 });
        await db.SaveChangesAsync();

        var controller = new HealthController(db, NullLogger<HealthController>.Instance);
        var ok = Assert.IsType<OkObjectResult>((await controller.Get(default)).Result);
        var body = Assert.IsType<HealthResponse>(ok.Value);

        Assert.Equal("healthy", body.Status);
        Assert.All(body.Klines.Where(x => x.Active), x => Assert.Equal("fresh", x.Status));
    }

    [Fact]
    public async Task Get_FlagsStaleAfterTwoMissedCloses()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var now = DateTimeOffset.UtcNow;
        var hourOpen = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        // Two consecutive 1h closes missed: newest stored open is >3 hours old.
        // -1s keeps the boundary-tick edge (age == exactly 3h) out of the test.
        db.Klines.Add(new Kline { Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = hourOpen.AddHours(-3).AddSeconds(-1).ToUnixTimeMilliseconds(), CloseTimeMs = hourOpen.AddHours(-2).ToUnixTimeMilliseconds() - 1, Open = 1, High = 1, Low = 1, Close = 1 });
        await db.SaveChangesAsync();

        var controller = new HealthController(db, NullLogger<HealthController>.Instance);
        var ok = Assert.IsType<OkObjectResult>((await controller.Get(default)).Result);
        var body = Assert.IsType<HealthResponse>(ok.Value);

        Assert.Equal("degraded", body.Status);
        Assert.Equal("stale", body.Klines.Single(x => x.Timeframe == "1h").Status);
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

        // Worker-written messages are only verbatim for an admin-key caller (see the next test).
        var body = (await GetWorkersAsync(db, AdminConfig(), AdminKey)).Body;

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
    public async Task Workers_ShowsWorkerWrittenDetailOnlyToValidAdminKey()
    {
        const string leakyError = "NpgsqlException: Failed to connect to 10.0.0.5:5432 | SocketException: Host=10.0.0.5;Username=btc_admin";
        const string leakyDetail = "failed: BTCUSDT/1h (PostgresException: 42P01: relation \"MlFeatureStores_tmp\" does not exist)";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var now = DateTime.UtcNow;
        db.WorkerHeartbeats.AddRange(
            new WorkerHeartbeat
            {
                WorkerName = "CausalSmartMoneyRebuildWorker", Status = "Failed",
                LastStartedAtUtc = now, LastFailedAtUtc = now, UpdatedAtUtc = now, LastError = leakyError
            },
            new WorkerHeartbeat
            {
                WorkerName = "MlDatasetBuilder", Status = "Partial",
                LastStartedAtUtc = now, LastSucceededAtUtc = now, UpdatedAtUtc = now,
                LastCycleAttempted = 3, LastCycleSucceeded = 2, LastCycleFailed = 1,
                LastCycleSkipped = 0, LastCycleRemaining = 0, LastCycleDetail = leakyDetail
            },
            new WorkerHeartbeat
            {
                WorkerName = "KlinesIngestionWorker", Status = "Succeeded",
                LastStartedAtUtc = now, LastSucceededAtUtc = now, UpdatedAtUtc = now
            },
            new WorkerHeartbeat
            {
                WorkerName = "RssIngestionService", Status = "Failed",
                LastStartedAtUtc = now, LastFailedAtUtc = now, UpdatedAtUtc = now, LastError = leakyError
            });
        await db.SaveChangesAsync();
        const string configDisabled = "Disabled by configuration (BackgroundWorkers:RssIngestionService:Enabled=false).";
        var configuration = AdminConfig(("BackgroundWorkers:RssIngestionService:Enabled", "false"));

        var admin = await GetWorkersAsync(db, configuration, AdminKey);
        Assert.Equal(leakyError, Worker(admin.Body, "CausalSmartMoneyRebuildWorker").Message);
        Assert.Equal(leakyDetail, Worker(admin.Body, "MlDatasetBuilder").Message);
        Assert.Null(Worker(admin.Body, "KlinesIngestionWorker").Message);
        Assert.Equal(configDisabled, Worker(admin.Body, "RssIngestionService").Message);
        Assert.Equal("No heartbeat has been recorded.", Worker(admin.Body, "IndexingBackgroundWorker").Message);

        // No key, wrong key, empty key; then a server without AdminApiKey (an empty header must not match it).
        var notConfigured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AdminApiKey"] = "", ["BackgroundWorkers:RssIngestionService:Enabled"] = "false" })
            .Build();
        foreach (var (config, header) in new (IConfiguration, string?)[]
                 {
                     (configuration, null), (configuration, "wrong-secret"), (configuration, ""),
                     (notConfigured, ""), (notConfigured, AdminKey)
                 })
        {
            var anonymous = await GetWorkersAsync(db, config, header);

            Assert.DoesNotContain("10.0.0.5:5432", anonymous.Json);
            Assert.DoesNotContain("Host=10.0.0.5", anonymous.Json);
            Assert.DoesNotContain("btc_admin", anonymous.Json);
            Assert.DoesNotContain("MlFeatureStores_tmp", anonymous.Json);
            Assert.DoesNotContain("Npgsql", anonymous.Json);
            Assert.Equal(HealthController.HiddenWorkerDetail, Worker(anonymous.Body, "CausalSmartMoneyRebuildWorker").Message);
            Assert.Equal(HealthController.HiddenWorkerDetail, Worker(anonymous.Body, "MlDatasetBuilder").Message);
            Assert.Null(Worker(anonymous.Body, "KlinesIngestionWorker").Message);
            Assert.Equal(configDisabled, Worker(anonymous.Body, "RssIngestionService").Message);
            Assert.Equal("No heartbeat has been recorded.", Worker(anonymous.Body, "IndexingBackgroundWorker").Message);
            // Status, timestamps and counters are the same as the admin view; only the message differs.
            Assert.Equal(admin.Body.Workers.Select(w => w with { Message = null, AgeSeconds = null }),
                anonymous.Body.Workers.Select(w => w with { Message = null, AgeSeconds = null }));
        }
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

    [Fact]
    public async Task Workers_ReportsConfigDisabledWorkerAsDisabledNotStale()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var old = DateTime.UtcNow.AddDays(-2);
        db.WorkerHeartbeats.AddRange(
            new WorkerHeartbeat { WorkerName = "RssIngestionService", Status = "Succeeded", LastStartedAtUtc = old, LastSucceededAtUtc = old, UpdatedAtUtc = old },
            new WorkerHeartbeat { WorkerName = "KlinesIngestionWorker", Status = "Succeeded", LastStartedAtUtc = old, LastSucceededAtUtc = old, UpdatedAtUtc = old });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundWorkers:RssIngestionService:Enabled"] = "false" })
            .Build();

        var controller = new HealthController(db, NullLogger<HealthController>.Instance, configuration: configuration);
        var ok = Assert.IsType<OkObjectResult>((await controller.Workers(default)).Result);
        var body = Assert.IsType<WorkerHealthResponse>(ok.Value);

        var rss = body.Workers.Single(w => w.Name == "RssIngestionService");
        Assert.Equal("disabled", rss.Status);
        Assert.Equal(old, rss.LastSucceededAtUtc);
        Assert.Equal("stale", body.Workers.Single(w => w.Name == "KlinesIngestionWorker").Status);
        Assert.True(BackgroundWorkerSwitch.IsEnabled(configuration, "KlinesIngestionWorker"));
    }

    private const string AdminKey = "server-secret";

    private static IConfiguration AdminConfig(params (string Key, string Value)[] extra) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(extra.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value))
                .Append(new KeyValuePair<string, string?>("AdminApiKey", AdminKey)))
            .Build();

    private static WorkerHealth Worker(WorkerHealthResponse body, string name) => body.Workers.Single(w => w.Name == name);

    // Goes through a real HttpContext so the X-Admin-Key header path is exercised; a wrong key must still be 200.
    private static async Task<(WorkerHealthResponse Body, string Json)> GetWorkersAsync(AppDbContext db, IConfiguration configuration, string? adminKeyHeader)
    {
        var controller = new HealthController(db, NullLogger<HealthController>.Instance, configuration: configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (adminKeyHeader is not null) controller.Request.Headers["X-Admin-Key"] = adminKeyHeader;

        var ok = Assert.IsType<OkObjectResult>((await controller.Workers(default)).Result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
        var body = Assert.IsType<WorkerHealthResponse>(ok.Value);
        return (body, JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
