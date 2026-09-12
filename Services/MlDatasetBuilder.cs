namespace Backend.Services;

/// <summary>
/// Background worker that periodically rebuilds the per-bar ML dataset
/// by delegating to <see cref="IMlDatasetService"/>.
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
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
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

        var symbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT" };
        foreach (var symbol in symbols)
        {
            foreach (var tf in _timeframes)
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
}
