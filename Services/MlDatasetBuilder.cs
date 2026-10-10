using Backend.Data;

namespace Backend.Services;

/// <summary>
/// Background worker that periodically rebuilds the per-bar ML dataset
/// by delegating to <see cref="IMlDatasetService"/>.
/// Reports a WorkerCycleReport per cycle so /api/health/workers distinguishes
/// succeeded / partial / failed instead of silently going stale.
/// </summary>
public class MlDatasetBuilder : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MlDatasetBuilder> _logger;
    private readonly IReadOnlyList<string> _timeframes;

    public MlDatasetBuilder(
        IServiceScopeFactory scopeFactory,
        ILogger<MlDatasetBuilder> logger,
        ProductionTimeframePolicy? timeframePolicy = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeframes = (timeframePolicy ?? new ProductionTimeframePolicy()).Active;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAtUtc = DateTime.UtcNow;
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ML dataset build cycle failed");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await WorkerHeartbeatStore.MarkFailedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(MlDatasetBuilder), startedAtUtc, DateTime.UtcNow, ex, stoppingToken);
                }
                catch (Exception heartbeatException) { _logger.LogWarning(heartbeatException, "Could not persist failed ML dataset heartbeat"); }
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<WorkerCycleReport> RunCycleAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        using (var scope = _scopeFactory.CreateScope())
        {
            await WorkerHeartbeatStore.MarkStartedAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(MlDatasetBuilder), startedAtUtc, cancellationToken);
        }

        var symbols = new[] { ProductionSymbolPolicy.Symbol };
        var attempted = 0;
        var succeeded = 0;
        var failedPairs = new List<string>();
        foreach (var symbol in symbols)
        {
            foreach (var tf in _timeframes)
            {
                attempted++;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var mlService = scope.ServiceProvider.GetRequiredService<IMlDatasetService>();
                    var built = await mlService.BuildAsync(symbol, tf, cancellationToken);
                    succeeded++;
                    _logger.LogInformation(
                        "ML dataset build completed for {Symbol} {Timeframe}: {Count} rows",
                        symbol, tf, built);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedPairs.Add($"{symbol}/{tf}");
                    _logger.LogWarning(ex, "Failed to build ML dataset for {Symbol} {Timeframe}", symbol, tf);
                }
            }
        }

        var outcome = failedPairs.Count == 0
            ? WorkerCycleOutcome.Succeeded
            : succeeded > 0
                ? WorkerCycleOutcome.Partial
                : WorkerCycleOutcome.Failed;
        var report = new WorkerCycleReport(
            outcome,
            Attempted: attempted,
            Succeeded: succeeded,
            Failed: failedPairs.Count,
            Detail: failedPairs.Count > 0 ? $"failed: {string.Join(", ", failedPairs)}" : null);

        using (var scope = _scopeFactory.CreateScope())
        {
            await WorkerHeartbeatStore.MarkCompletedAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(MlDatasetBuilder), startedAtUtc, DateTime.UtcNow, report, cancellationToken);
        }
        _logger.LogInformation(
            "ML dataset build cycle: status={Status} attempted={Attempted} succeeded={Succeeded} failed={Failed} detail={Detail}",
            report.Outcome, report.Attempted, report.Succeeded, report.Failed, report.Detail ?? "-");
        return report;
    }
}
