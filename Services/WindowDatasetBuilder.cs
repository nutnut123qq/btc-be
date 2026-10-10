using Backend.Data;

namespace Backend.Services;

/// <summary>
/// Background worker that periodically builds the per-window classification dataset
/// by delegating to <see cref="IWindowDatasetService"/>.
/// Reports a WorkerCycleReport per cycle so /api/health/workers distinguishes
/// succeeded / partial / failed instead of silently going stale.
/// </summary>
public class WindowDatasetBuilder : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WindowDatasetBuilder> _logger;

    private static readonly string[] Symbols = { "BTCUSDT" };
    private readonly IReadOnlyList<string> _timeframes;

    public WindowDatasetBuilder(
        IServiceScopeFactory scopeFactory,
        ILogger<WindowDatasetBuilder> logger,
        ProductionTimeframePolicy? timeframePolicy = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeframes = (timeframePolicy ?? new ProductionTimeframePolicy()).Active;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let MlDatasetBuilder finish its first cycle before we start.
        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);

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
                _logger.LogError(ex, "Window dataset build cycle failed");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await WorkerHeartbeatStore.MarkFailedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(WindowDatasetBuilder), startedAtUtc, DateTime.UtcNow, ex, stoppingToken);
                }
                catch (Exception heartbeatException) { _logger.LogWarning(heartbeatException, "Could not persist failed window dataset heartbeat"); }
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
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
                scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(WindowDatasetBuilder), startedAtUtc, cancellationToken);
        }

        var attempted = 0;
        var succeeded = 0;
        var failedPairs = new List<string>();
        foreach (var symbol in Symbols)
        {
            foreach (var timeframe in _timeframes)
            {
                attempted++;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IWindowDatasetService>();
                    var count = await service.BuildAllAsync(symbol, timeframe, cancellationToken);
                    succeeded++;
                    _logger.LogInformation(
                        "Window dataset cycle completed for {Symbol} {Timeframe}: {Count} samples",
                        symbol, timeframe, count);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedPairs.Add($"{symbol}/{timeframe}");
                    _logger.LogWarning(ex,
                        "Failed to build window dataset for {Symbol} {Timeframe}",
                        symbol, timeframe);
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
                scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(WindowDatasetBuilder), startedAtUtc, DateTime.UtcNow, report, cancellationToken);
        }
        _logger.LogInformation(
            "Window dataset build cycle: status={Status} attempted={Attempted} succeeded={Succeeded} failed={Failed} detail={Detail}",
            report.Outcome, report.Attempted, report.Succeeded, report.Failed, report.Detail ?? "-");
        return report;
    }
}
