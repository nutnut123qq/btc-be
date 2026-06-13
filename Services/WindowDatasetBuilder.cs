using Backend.Data;

namespace Backend.Services;

/// <summary>
/// Background worker that periodically builds the per-window classification dataset
/// by delegating to <see cref="IWindowDatasetService"/>.
/// </summary>
public class WindowDatasetBuilder : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WindowDatasetBuilder> _logger;

    private static readonly string[] Timeframes = { "15m", "1h" };

    public WindowDatasetBuilder(
        IServiceScopeFactory scopeFactory,
        ILogger<WindowDatasetBuilder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let MlDatasetBuilder finish its first cycle before we start.
        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Window dataset build cycle failed");
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
        var service = scope.ServiceProvider.GetRequiredService<IWindowDatasetService>();

        const string symbol = "BTCUSDT";
        foreach (var timeframe in Timeframes)
        {
            try
            {
                var count = await service.BuildAllAsync(symbol, timeframe, cancellationToken);
                _logger.LogInformation(
                    "Window dataset cycle completed for {Symbol} {Timeframe}: {Count} samples",
                    symbol, timeframe, count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to build window dataset for {Symbol} {Timeframe}",
                    symbol, timeframe);
            }
        }
    }
}
