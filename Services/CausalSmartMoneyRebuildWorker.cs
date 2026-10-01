using System.Collections.Concurrent;
using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>Advances at most one bounded causal SMC batch per active timeframe per cycle.</summary>
public sealed class CausalSmartMoneyRebuildWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<CausalSmartMoneyRebuildWorker> logger;
    private readonly CausalSmartMoneyRebuildOptions options;
    private readonly TimeProvider timeProvider;
    private readonly IReadOnlyList<string> timeframes;

    public CausalSmartMoneyRebuildWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CausalSmartMoneyRebuildWorker> logger,
        IOptions<CausalSmartMoneyRebuildOptions> options,
        TimeProvider? timeProvider = null,
        ProductionTimeframePolicy? timeframePolicy = null)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.options = options.Value;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        timeframes = (timeframePolicy ?? new ProductionTimeframePolicy()).Active;
        if (this.options.BatchCandles is < 1 or > CausalSmartMoneyRebuildService.MaximumBatchCandles)
            throw new InvalidOperationException($"CausalSmartMoneyRebuild:BatchCandles must be between 1 and {CausalSmartMoneyRebuildService.MaximumBatchCandles}.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.InitialDelaySeconds)), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.CycleMinutes)), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // normal shutdown
            }
        }
    }

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        var started = timeProvider.GetUtcNow().UtcDateTime;
        var failures = new ConcurrentQueue<string>();
        using (var scope = scopeFactory.CreateScope())
        {
            await WorkerHeartbeatStore.MarkStartedAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(CausalSmartMoneyRebuildWorker), started, ct);
        }

        foreach (var timeframe in timeframes)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ICausalSmartMoneyRebuildService>();
                var result = await service.RebuildAsync(new CausalSmartMoneyRebuildRequest
                {
                    Symbol = ProductionSymbolPolicy.Symbol,
                    Timeframe = timeframe,
                    DryRun = false,
                    MaxCandles = options.BatchCandles
                }, ct);
                logger.LogInformation(
                    "Causal SMC upkeep {Symbol} {Timeframe}: status={Status}, candidates={Candidates}, inserted={Inserted}, updated={Updated}",
                    result.Symbol, result.Timeframe, result.Status, result.CandidateCandles, result.InsertedEvents, result.UpdatedEvents);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Enqueue($"{timeframe}: {ex.GetType().Name}: {ex.Message}");
                logger.LogWarning(ex, "Causal SMC upkeep failed for BTCUSDT {Timeframe}; remaining timeframes will continue", timeframe);
            }
        }

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var completed = timeProvider.GetUtcNow().UtcDateTime;
            if (failures.IsEmpty)
                await WorkerHeartbeatStore.MarkSucceededAsync(db, nameof(CausalSmartMoneyRebuildWorker), started, completed, ct);
            else
                await WorkerHeartbeatStore.MarkFailedAsync(db, nameof(CausalSmartMoneyRebuildWorker), started, completed,
                    new InvalidOperationException(string.Join(" | ", failures)), ct);
        }
    }
}
