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
        Assert.Contains("BTCUSDT/4h", report.Detail);
        await using var scope = services.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync(x => x.WorkerName == nameof(MlDatasetBuilder));
        Assert.Equal("Partial", heartbeat.Status);
        Assert.Equal(1, heartbeat.LastCycleFailed);
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
        Assert.Contains("BTCUSDT/1d", report.Detail);
        await using var scope = services.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .WorkerHeartbeats.SingleAsync(x => x.WorkerName == nameof(WindowDatasetBuilder));
        Assert.Equal("Partial", heartbeat.Status);
        Assert.Equal(1, heartbeat.LastCycleFailed);
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

    private static ServiceProvider BuildMlServices(StubMlDatasetService stub)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ServiceCollection()
            .AddSingleton<IMlDatasetService>(stub)
            .AddScoped(_ => new AppDbContext(opts))
            .BuildServiceProvider();
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
        public List<string> Calls { get; } = new();
        public int RowsBuilt { get; set; } = 1;

        public Task<int> BuildAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            Calls.Add($"{symbol}/{timeframe}");
            if (FailingTimeframes.Contains(timeframe))
                throw new InvalidOperationException($"build failed for {symbol}/{timeframe}");
            return Task.FromResult(RowsBuilt);
        }
    }

    private sealed class StubWindowDatasetService : IWindowDatasetService
    {
        public HashSet<string> FailingTimeframes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = new();
        public int Samples { get; set; } = 1;

        public Task<int> BuildAllAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            Calls.Add($"{symbol}/{timeframe}");
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
