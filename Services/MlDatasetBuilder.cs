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
        var pcaService = scope.ServiceProvider.GetRequiredService<WindowVectorPcaService>();

        const string symbol = "BTCUSDT";
        foreach (var tf in new[] { "15m", "1h" })
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

        try
        {
            await pcaService.ComputeAndStoreAsync(symbol, "15m", "returns_shape", 25, 5, cancellationToken);
            await pcaService.ComputeAndStoreAsync(symbol, "1h", "returns_shape", 25, 5, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PCA computation failed");
        }
    }
}
