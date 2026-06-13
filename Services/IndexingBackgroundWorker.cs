using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Tự động rebuild các index (pattern, window vector, volume stats) định kỳ
/// để dữ liệu phân tích không bị stale.
/// </summary>
public class IndexingBackgroundWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IndexingBackgroundWorker> _logger;

    public IndexingBackgroundWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<IndexingBackgroundWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay 30s để app khởi động xong
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Indexing background cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // shutdown
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var binance = scope.ServiceProvider.GetRequiredService<IBinanceKlinesService>();
        var patternIndexer = scope.ServiceProvider.GetRequiredService<ICandlePatternIndexer>();
        var vectorIndexer = scope.ServiceProvider.GetRequiredService<IWindowVectorIndexer>();
        var volumeIndexer = scope.ServiceProvider.GetRequiredService<CandleVolumeIndexer>();
        var techIndexer = scope.ServiceProvider.GetRequiredService<TechnicalIndicatorIndexer>();
        var marketIndexer = scope.ServiceProvider.GetRequiredService<MarketMetricsIndexer>();
        var patternSeqIndexer = scope.ServiceProvider.GetRequiredService<CandlePatternSequenceIndexer>();

        const string symbol = "BTCUSDT";

        // 1. Candle patterns cho các timeframe chính
        foreach (var tf in new[] { "15m", "1h" })
        {
            try
            {
                var klines = await binance.GetKlinesAsync(symbol, tf, 5000, cancellationToken: cancellationToken);
                var indexed = await patternIndexer.IndexAsync(symbol, tf, klines, cancellationToken);
                _logger.LogInformation("Auto-indexed {Indexed} candle patterns for {Symbol} {Timeframe}", indexed, symbol, tf);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-index candle patterns for {Symbol} {Timeframe}", symbol, tf);
            }
        }

        // 2. Window vectors cho 15m (feature types và window sizes phổ biến)
        var features = new[] { "open", "close", "all", "returns_shape", "returns_log", "volume_norm", "volatility", "trend" };
        var windowSizes = new[] { 10, 15, 25 };
        foreach (var ws in windowSizes)
        {
            foreach (var ft in features)
            {
                try
                {
                    var upserted = await vectorIndexer.BuildFullAsync(symbol, "15m", ft, 5000, ws, cancellationToken);
                    _logger.LogInformation("Auto-built {Upserted} window vectors for {Symbol} 15m {Feature} ws={WindowSize}", upserted, symbol, ft, ws);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to auto-build window vectors for {Symbol} 15m {Feature} ws={WindowSize}", symbol, ft, ws);
                }
            }
        }

        // 3. Volume stats cho 15m và 1h
        foreach (var tf in new[] { "15m", "1h" })
        {
            try
            {
                var klines = await binance.GetKlinesAsync(symbol, tf, 5000, cancellationToken: cancellationToken);
                var indexed = await volumeIndexer.IndexAsync(symbol, tf, klines, cancellationToken);
                _logger.LogInformation("Auto-indexed {Indexed} volume stats for {Symbol} {Timeframe}", indexed, symbol, tf);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-index volume stats for {Symbol} {Timeframe}", symbol, tf);
            }
        }

        // 4. Technical indicators cho 15m và 1h
        foreach (var tf in new[] { "15m", "1h" })
        {
            try
            {
                var indexed = await techIndexer.IndexAsync(symbol, tf, cancellationToken);
                _logger.LogInformation("Auto-indexed {Indexed} technical indicators for {Symbol} {Timeframe}", indexed, symbol, tf);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-index technical indicators for {Symbol} {Timeframe}", symbol, tf);
            }
        }

        // 5. Market metrics (funding rate, open interest)
        try
        {
            await marketIndexer.IndexFundingRateAsync(symbol, cancellationToken);
            await marketIndexer.IndexOpenInterestAsync(symbol, "1h", cancellationToken);
            await marketIndexer.IndexLiquidationsAsync(symbol, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-index market metrics for {Symbol}", symbol);
        }

        // 6. Pattern sequences
        foreach (var tf in new[] { "15m", "1h" })
        {
            try
            {
                var indexed = await patternSeqIndexer.IndexAsync(symbol, tf, cancellationToken);
                _logger.LogInformation("Auto-indexed {Indexed} pattern sequences for {Symbol} {Timeframe}", indexed, symbol, tf);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-index pattern sequences for {Symbol} {Timeframe}", symbol, tf);
            }
        }
    }
}
