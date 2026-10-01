using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Backend.Tests;

public sealed class CausalSmartMoneyRebuildWorkerTests
{
    [Fact]
    public async Task Cycle_invokes_each_active_timeframe_and_isolates_failure()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(x => x.UseInMemoryDatabase(databaseName));
        var fake = new FakeRebuildService("4h");
        services.AddSingleton<ICausalSmartMoneyRebuildService>(fake);
        await using var provider = services.BuildServiceProvider();
        var worker = new CausalSmartMoneyRebuildWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CausalSmartMoneyRebuildWorker>.Instance,
            Microsoft.Extensions.Options.Options.Create(new CausalSmartMoneyRebuildOptions { BatchCandles = 17 }),
            TimeProvider.System,
            new ProductionTimeframePolicy());

        await worker.RunCycleAsync(default);

        Assert.Equal(ProductionTimeframePolicy.Defaults, fake.Calls.Select(x => x.Timeframe).ToArray());
        Assert.All(fake.Calls, x =>
        {
            Assert.False(x.DryRun);
            Assert.Equal(17, x.MaxCandles);
            Assert.Equal("BTCUSDT", x.Symbol);
        });
        await using var scope = provider.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WorkerHeartbeats
            .SingleAsync(x => x.WorkerName == nameof(CausalSmartMoneyRebuildWorker));
        Assert.Equal("Failed", heartbeat.Status);
        Assert.Contains("4h", heartbeat.LastError);
    }

    private sealed class FakeRebuildService(string failingTimeframe) : ICausalSmartMoneyRebuildService
    {
        public List<CausalSmartMoneyRebuildRequest> Calls { get; } = [];

        public Task<CausalSmartMoneyRebuildResult> RebuildAsync(
            CausalSmartMoneyRebuildRequest request,
            CancellationToken ct = default)
        {
            Calls.Add(request);
            if (request.Timeframe == failingTimeframe) throw new InvalidOperationException("simulated failure");
            return Task.FromResult(new CausalSmartMoneyRebuildResult
            {
                Symbol = request.Symbol,
                Timeframe = request.Timeframe,
                Status = "checkpointed",
                CandidateCandles = request.MaxCandles
            });
        }

        public Task<CausalSmartMoneyCoverageResponse> GetCoverageAsync(
            string symbol,
            string timeframe,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
