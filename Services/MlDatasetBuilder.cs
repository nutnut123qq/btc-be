namespace Backend.Services;

/// <summary>
/// Background worker that periodically rebuilds the per-bar ML dataset
/// by delegating to <see cref="IMlDatasetService"/>.
/// </summary>
public class MlDatasetBuilder : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MlDatasetBuilder> _logger;

    public MlDatasetBuilder(
        IServiceScopeFactory scopeFactory,
        ILogger<MlDatasetBuilder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ML dataset build cycle failed");
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

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var mlService = scope.ServiceProvider.GetRequiredService<IMlDatasetService>();

        const string symbol = "BTCUSDT";
        // 1m is intentionally excluded: it has ~139k gaps, is very noisy, and consumes the most RAM.
        // We keep 5m–1d as the clean training timeframes. Raw Klines 1m is still ingested for possible future backfill.
        foreach (var tf in new[] { "5m", "15m", "30m", "1h", "4h", "1d" })
        {
            try
            {
                await mlService.BuildAsync(symbol, tf, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build ML dataset for {Symbol} {Timeframe}", symbol, tf);
            }
        }
    }
}
