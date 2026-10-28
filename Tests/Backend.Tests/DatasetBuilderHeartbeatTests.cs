using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

/// <summary>
/// WORKERHB-1: MlDatasetBuilder/WindowDatasetBuilder must persist a
/// WorkerHeartbeat per cycle so /api/health/workers + ops/status.ps1 can see
/// them (silent-stale blind spot — they were registered but never heartbeated).
/// </summary>
public class DatasetBuilderHeartbeatTests
{
    [Fact]
    public async Task MlRunCycleAsync_AllPairsSucceed_WritesSucceededHeartbeat()
    {
        var stub = new StubMlDatasetService();
        var services = BuildMlServices(stub);
        var worker = new MlDatasetBuilder(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MlDatasetBuilder>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Succeeded, report.Outcome);
        Assert.Equal(3, report.Attempted); // BTCUSDT x {1h,4h,1d}
        Assert.Equal(3, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.Equal(
            new[] { "BTCUSDT/1h", "BTCUSDT/4h", "BTCUSDT/1d" },
            stub.Calls);
        await using var scope = services.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync(x => x.WorkerName == nameof(MlDatasetBuilder));
        Assert.Equal("Succeeded", heartbeat.Status);
        Assert.NotNull(heartbeat.LastSucceededAtUtc);
        Assert.Null(heartbeat.LastFailedAtUtc);
        Assert.Equal(3, heartbeat.LastCycleAttempted);
        Assert.Equal(3, heartbeat.LastCycleSucceeded);
        Assert.Equal(0, heartbeat.LastCycleFailed);
    }

    [Fact]
    public async Task MlRunCycleAsync_OneTimeframeFails_ReportsPartial()
    {
        var stub = new StubMlDatasetService();
        stub.FailingTimeframes.Add("4h");
        var services = BuildMlServices(stub);
        var worker = new MlDatasetBuilder(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MlDatasetBuilder>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Partial, report.Outcome);
        Assert.Equal(3, report.Attempted);
        Assert.Equal(2, report.Succeeded);
        Assert.Equal(1, report.Failed);
        const string expectedDetail = "failed: BTCUSDT/4h (InvalidOperationException: build failed for BTCUSDT/4h)";
        Assert.Equal(expectedDetail, report.Detail);
        await using var scope = services.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync(x => x.WorkerName == nameof(MlDatasetBuilder));
        Assert.Equal("Partial", heartbeat.Status);
        Assert.Equal(1, heartbeat.LastCycleFailed);
        Assert.Equal(expectedDetail, heartbeat.LastCycleDetail);
        Assert.Null(heartbeat.LastError);
        // Partial still proves liveness so the watchdog allow-list stays green.
        Assert.NotNull(heartbeat.LastSucceededAtUtc);
        Assert.Null(heartbeat.LastFailedAtUtc);
    }

    [Fact]
    public async Task MlRunCycleAsync_AllPairsFail_ReportsFailed()
    {
        var stub = new StubMlDatasetService();
        foreach (var tf in new[] { "1h", "4h", "1d" }) stub.FailingTimeframes.Add(tf);
        var services = BuildMlServices(stub);
        var worker = new MlDatasetBuilder(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MlDatasetBuilder>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Failed, report.Outcome);
        Assert.Equal(3, report.Attempted);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(3, report.Failed);
        await using var scope = services.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync(x => x.WorkerName == nameof(MlDatasetBuilder));
        Assert.Equal("Failed", heartbeat.Status);
        Assert.NotNull(heartbeat.LastFailedAtUtc);
        Assert.Null(heartbeat.LastSucceededAtUtc);
        Assert.NotNull(heartbeat.LastError);
    }

    [Fact]
    public async Task WindowRunCycleAsync_AllPairsSucceed_WritesSucceededHeartbeat()
    {
        var stub = new StubWindowDatasetService();
        var services = BuildWindowServices(stub);
        var worker = new WindowDatasetBuilder(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WindowDatasetBuilder>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Succeeded, report.Outcome);
        Assert.Equal(3, report.Attempted); // BTCUSDT x {1h,4h,1d}
        Assert.Equal(3, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.Equal(
            new[] { "BTCUSDT/1h", "BTCUSDT/4h", "BTCUSDT/1d" },
            stub.Calls);
        await using var scope = services.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync(x => x.WorkerName == nameof(WindowDatasetBuilder));
        Assert.Equal("Succeeded", heartbeat.Status);
        Assert.NotNull(heartbeat.LastSucceededAtUtc);
        Assert.Equal(3, heartbeat.LastCycleAttempted);
        Assert.Equal(3, heartbeat.LastCycleSucceeded);
    }

    [Fact]
    public async Task WindowRunCycleAsync_OneTimeframeFails_ReportsPartial()
    {
        var stub = new StubWindowDatasetService();
        stub.FailingTimeframes.Add("1d");
        var services = BuildWindowServices(stub);
        var worker = new WindowDatasetBuilder(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WindowDatasetBuilder>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Partial, report.Outcome);
        Assert.Equal(3, report.Attempted);
        Assert.Equal(2, report.Succeeded);
        Assert.Equal(1, report.Failed);
        const string expectedDetail = "failed: BTCUSDT/1d (InvalidOperationException: build failed for BTCUSDT/1d)";
        Assert.Equal(expectedDetail, report.Detail);
        await using var scope = services.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync(x => x.WorkerName == nameof(WindowDatasetBuilder));
        Assert.Equal("Partial", heartbeat.Status);
        Assert.Equal(1, heartbeat.LastCycleFailed);
        Assert.Equal(expectedDetail, heartbeat.LastCycleDetail);
        Assert.Null(heartbeat.LastError);
        Assert.NotNull(heartbeat.LastSucceededAtUtc);
    }

    [Fact]
    public async Task Workers_ListsBothDatasetBuildersAsExpectedWorkers()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var controller = new HealthController(db, NullLogger<HealthController>.Instance);

        var ok = Assert.IsType<OkObjectResult>((await controller.Workers(default)).Result);
        var body = Assert.IsType<WorkerHealthResponse>(ok.Value);

        var ml = body.Workers.Single(w => w.Name == nameof(MlDatasetBuilder));
        Assert.Equal("never", ml.Status);
        Assert.Equal(140 * 60, ml.MaxAgeSeconds);

        var window = body.Workers.Single(w => w.Name == nameof(WindowDatasetBuilder));
        Assert.Equal("never", window.Status);
        Assert.Equal(8 * 3600, window.MaxAgeSeconds);
    }

    [Fact]
    public async Task MlRunCycleAsync_AllPairsFail_RecordsEachPairsErrorOnOneLine()
    {
        var stub = new StubMlDatasetService();
        stub.Errors["1h"] = new IOException("disk full\r\n  on D:");
        stub.Errors["4h"] = new InvalidOperationException("save failed", new TimeoutException("db timeout"));
        stub.FailingTimeframes.Add("1d");
        var services = BuildMlServices(stub);
        var worker = new MlDatasetBuilder(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MlDatasetBuilder>.Instance);

        var report = await worker.RunCycleAsync(default);

        const string expected = "failed: BTCUSDT/1h (IOException: disk full on D:), "
            + "BTCUSDT/4h (InvalidOperationException: save failed | TimeoutException: db timeout), "
            + "BTCUSDT/1d (InvalidOperationException: build failed for BTCUSDT/1d)";
        Assert.Equal((WorkerCycleOutcome.Failed, 3, 0, 3), (report.Outcome, report.Attempted, report.Succeeded, report.Failed));
        Assert.Equal(expected, report.Detail);
        var heartbeat = await ReadHeartbeatAsync(services, nameof(MlDatasetBuilder));
        Assert.Equal("Failed", heartbeat.Status);
        Assert.Equal(expected, heartbeat.LastCycleDetail);
        Assert.Equal(expected, heartbeat.LastError);
    }

    [Fact]
    public async Task WindowRunCycleAsync_AllPairsFail_RecordsEachPairsErrorOnOneLine()
    {
        var stub = new StubWindowDatasetService();
        stub.FailingTimeframes.Add("1h");
        stub.Errors["4h"] = new TimeoutException("");
        stub.Errors["1d"] = new InvalidOperationException("window\nbuild failed", new IOException("file locked\r\nby indexer"));
        var services = BuildWindowServices(stub);
        var worker = new WindowDatasetBuilder(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WindowDatasetBuilder>.Instance);

        var report = await worker.RunCycleAsync(default);

        const string expected = "failed: BTCUSDT/1h (InvalidOperationException: build failed for BTCUSDT/1h), "
            + "BTCUSDT/4h (TimeoutException), "
            + "BTCUSDT/1d (InvalidOperationException: window build failed | IOException: file locked by indexer)";
        Assert.Equal((WorkerCycleOutcome.Failed, 3, 0, 3), (report.Outcome, report.Attempted, report.Succeeded, report.Failed));
        Assert.Equal(expected, report.Detail);
        var heartbeat = await ReadHeartbeatAsync(services, nameof(WindowDatasetBuilder));
        Assert.Equal("Failed", heartbeat.Status);
        Assert.Equal(expected, heartbeat.LastCycleDetail);
        Assert.Equal(expected, heartbeat.LastError);
    }

    [Fact]
    public async Task RunCycleAsync_LongErrors_KeepEveryPairAndReasonWithinTheDetailLimit()
    {
        var longError = new InvalidOperationException("first line\n" + new string('x', 5000));
        var ml = new StubMlDatasetService();
        var window = new StubWindowDatasetService();
        foreach (var tf in new[] { "1h", "4h", "1d" })
        {
            ml.Errors[tf] = longError;
            window.Errors[tf] = longError;
        }
        var mlServices = BuildMlServices(ml);
        var windowServices = BuildWindowServices(window);

        await new MlDatasetBuilder(mlServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<MlDatasetBuilder>.Instance)
            .RunCycleAsync(default);
        await new WindowDatasetBuilder(windowServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<WindowDatasetBuilder>.Instance)
            .RunCycleAsync(default);

        var reason = ("InvalidOperationException: first line " + new string('x', 5000))[..300];
        var expected = $"failed: BTCUSDT/1h ({reason}), BTCUSDT/4h ({reason}), BTCUSDT/1d ({reason})";
        foreach (var heartbeat in new[]
                 {
                     await ReadHeartbeatAsync(mlServices, nameof(MlDatasetBuilder)),
                     await ReadHeartbeatAsync(windowServices, nameof(WindowDatasetBuilder))
                 })
        {
            Assert.Equal(expected, heartbeat.LastCycleDetail);
            Assert.True(heartbeat.LastCycleDetail!.Length <= 1000);
            Assert.Equal(expected, heartbeat.LastError);
        }
    }

    [Fact]
    public async Task RecordCycleFailureAsync_WritesFailedHeartbeatInMarkFailedFormat_AndClearsCounters()
    {
        var ml = new StubMlDatasetService();
        ml.FailingTimeframes.Add("4h");
        var window = new StubWindowDatasetService();
        window.FailingTimeframes.Add("1d");
        var mlServices = BuildMlServices(ml);
        var windowServices = BuildWindowServices(window);
        var mlWorker = new MlDatasetBuilder(mlServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<MlDatasetBuilder>.Instance);
        var windowWorker = new WindowDatasetBuilder(windowServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<WindowDatasetBuilder>.Instance);
        // A Partial cycle first, so the crash has counters and a detail to clear.
        await mlWorker.RunCycleAsync(default);
        await windowWorker.RunCycleAsync(default);
        var crash = new InvalidOperationException("cycle crashed\nretry later", new TimeoutException("db"));
        var startedAtUtc = DateTime.UtcNow.AddSeconds(-1);

        await mlWorker.RecordCycleFailureAsync(startedAtUtc, crash, default);
        await windowWorker.RecordCycleFailureAsync(startedAtUtc, crash, default);

        foreach (var heartbeat in new[]
                 {
                     await ReadHeartbeatAsync(mlServices, nameof(MlDatasetBuilder)),
                     await ReadHeartbeatAsync(windowServices, nameof(WindowDatasetBuilder))
                 })
        {
            Assert.Equal("Failed", heartbeat.Status);
            Assert.Equal("InvalidOperationException: cycle crashed retry later | TimeoutException: db", heartbeat.LastError);
            Assert.NotNull(heartbeat.LastFailedAtUtc);
            Assert.True(heartbeat.LastDurationMs >= 1000);
            Assert.Null(heartbeat.LastCycleAttempted);
            Assert.Null(heartbeat.LastCycleSucceeded);
            Assert.Null(heartbeat.LastCycleFailed);
            Assert.Null(heartbeat.LastCycleSkipped);
            Assert.Null(heartbeat.LastCycleRemaining);
            Assert.Null(heartbeat.LastCycleDetail);
        }
    }

    [Fact]
    public async Task MlRecordCycleFailureAsync_HeartbeatStoreThrows_DoesNotThrow()
    {
        var worker = new MlDatasetBuilder(SaveFailingScopes(), NullLogger<MlDatasetBuilder>.Instance);

        await worker.RecordCycleFailureAsync(DateTime.UtcNow, new InvalidOperationException("cycle crashed"), default);
    }

    [Fact]
    public async Task WindowRecordCycleFailureAsync_HeartbeatStoreThrows_DoesNotThrow()
    {
        var worker = new WindowDatasetBuilder(SaveFailingScopes(), NullLogger<WindowDatasetBuilder>.Instance);

        await worker.RecordCycleFailureAsync(DateTime.UtcNow, new InvalidOperationException("cycle crashed"), default);
    }

    // WORKERHB-2: pins MarkFailedAsync's LastError text so the shared formatter refactor keeps it
    // character-identical; this test was run green on base 29ca6fd before the refactor.
    [Fact]
    public async Task MarkFailedAsync_LastError_KeepsItsSingleLineFormat()
    {
        var cases = new (Exception Error, string Expected)[]
        {
            (new InvalidOperationException("plain"), "InvalidOperationException: plain"),
            (new InvalidOperationException("  line one \r\n\r\n  line two\n"), "InvalidOperationException: line one line two"),
            (new InvalidOperationException(""), "InvalidOperationException"),
            (new IOException(" \r\n "), "IOException"),
            (new InvalidOperationException("outer", new IOException("mid", new TimeoutException("root\nline"))),
                "InvalidOperationException: outer | TimeoutException: root line"),
            (new InvalidOperationException("", new IOException("")), "InvalidOperationException: | IOException:"),
            (new InvalidOperationException(new string('x', 2000)), "InvalidOperationException: " + new string('x', 973)),
        };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        foreach (var (error, expected) in cases)
        {
            await WorkerHeartbeatStore.MarkFailedAsync(db, "pin", DateTime.UtcNow, DateTime.UtcNow, error, default);
            Assert.Equal(expected, (await db.WorkerHeartbeats.SingleAsync()).LastError);
        }
    }

    // PANELCLAMP-1: heartbeat cuts land on UTF-16 indexes; a surrogate pair straddling the limit must not
    // leave its high half behind (strict UTF-8 rejects it), and BMP-only text is cut exactly as before.
    [Fact]
    public async Task HeartbeatCuts_NeverEndOnHalfASurrogatePair()
    {
        const string pair = "\U0001F600"; // one code point = two UTF-16 units
        const string prefix = "InvalidOperationException: ";
        var strictUtf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        void AssertCleanCut(string? cut, int max)
        {
            Assert.NotNull(cut);
            Assert.True(cut!.Length <= max);
            Assert.False(char.IsHighSurrogate(cut[^1]));
            strictUtf8.GetBytes(cut);
        }

        foreach (var max in new[] { 300, 1000 })
        {
            var room = max - prefix.Length;
            // Pair straddles the limit: its high half goes too. Pair ends exactly at the limit: kept whole.
            var straddle = WorkerHeartbeatStore.FormatError(new InvalidOperationException(new string('x', room - 1) + pair + "tail"), max);
            Assert.Equal(prefix + new string('x', room - 1), straddle);
            AssertCleanCut(straddle, max);
            var fits = WorkerHeartbeatStore.FormatError(new InvalidOperationException(new string('x', room - 2) + pair + "tail"), max);
            Assert.Equal(prefix + new string('x', room - 2) + pair, fits);
            AssertCleanCut(fits, max);
            Assert.Equal(prefix + new string('x', room),
                WorkerHeartbeatStore.FormatError(new InvalidOperationException(new string('x', 5000)), max));
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var now = DateTime.UtcNow;

        await WorkerHeartbeatStore.MarkFailedAsync(db, "pin", now, now,
            new InvalidOperationException(new string('x', 1000 - prefix.Length - 1) + pair), default);
        var crashed = await db.WorkerHeartbeats.SingleAsync();
        Assert.Equal(prefix + new string('x', 1000 - prefix.Length - 1), crashed.LastError);
        AssertCleanCut(crashed.LastError, 1000);

        foreach (var outcome in new[] { WorkerCycleOutcome.Partial, WorkerCycleOutcome.Failed })
        {
            await WorkerHeartbeatStore.MarkCompletedAsync(db, "pin", now, now,
                new WorkerCycleReport(outcome, Detail: new string('y', 999) + pair + "tail"), default);
            var row = await db.WorkerHeartbeats.SingleAsync();
            Assert.Equal(new string('y', 999), row.LastCycleDetail);
            AssertCleanCut(row.LastCycleDetail, 1000);
            Assert.Equal(outcome == WorkerCycleOutcome.Failed ? row.LastCycleDetail : null, row.LastError);

            await WorkerHeartbeatStore.MarkCompletedAsync(db, "pin", now, now,
                new WorkerCycleReport(outcome, Detail: new string('y', 1500)), default);
            Assert.Equal(new string('y', 1000), (await db.WorkerHeartbeats.SingleAsync()).LastCycleDetail);
        }
    }

    private static ServiceProvider BuildMlServices(StubMlDatasetService stub)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ServiceCollection()
            .AddSingleton<IMlDatasetService>(stub)
            .AddScoped(_ => new AppDbContext(opts))
            .BuildServiceProvider();
    }

    private static async Task<WorkerHeartbeat> ReadHeartbeatAsync(ServiceProvider services, string workerName)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync(x => x.WorkerName == workerName);
    }

    private static IServiceScopeFactory SaveFailingScopes()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ServiceCollection()
            .AddScoped<AppDbContext>(_ => new SaveFailingDbContext(opts))
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class SaveFailingDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
            => throw new IOException("heartbeat database unavailable");
    }

    private static ServiceProvider BuildWindowServices(StubWindowDatasetService stub)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ServiceCollection()
            .AddSingleton<IWindowDatasetService>(stub)
            .AddScoped(_ => new AppDbContext(opts))
            .BuildServiceProvider();
    }

    private sealed class StubMlDatasetService : IMlDatasetService
    {
        public HashSet<string> FailingTimeframes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = new();
        public int RowsBuilt { get; set; } = 1;

        public Task<int> BuildAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            Calls.Add($"{symbol}/{timeframe}");
            if (Errors.TryGetValue(timeframe, out var error))
                throw error;
            if (FailingTimeframes.Contains(timeframe))
                throw new InvalidOperationException($"build failed for {symbol}/{timeframe}");
            return Task.FromResult(RowsBuilt);
        }
    }

    private sealed class StubWindowDatasetService : IWindowDatasetService
    {
        public HashSet<string> FailingTimeframes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = new();
        public int Samples { get; set; } = 1;

        public Task<int> BuildAllAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            Calls.Add($"{symbol}/{timeframe}");
            if (Errors.TryGetValue(timeframe, out var error))
                throw error;
            if (FailingTimeframes.Contains(timeframe))
                throw new InvalidOperationException($"build failed for {symbol}/{timeframe}");
            return Task.FromResult(Samples);
        }

        public Task<int> BuildAsync(string symbol, string timeframe, int windowSize, string horizon, int? maxSamples = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<int> BuildHorizonAsync(string symbol, string timeframe, string horizon, int? maxSamplesPerWindowSize = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<(float[] Vector, long WindowStartMs, long WindowEndMs)?> BuildLatestFeatureVectorAsync(string symbol, string timeframe, int windowSize, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
