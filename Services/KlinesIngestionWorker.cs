using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Tự động ingest klines từ Binance vào DB để phục vụ train AI & backtest.
/// </summary>
public class KlinesIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KlinesIngestionWorker> _logger;

    public KlinesIngestionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<KlinesIngestionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Klines ingestion cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
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
        var binance = scope.ServiceProvider.GetRequiredService<IBinanceKlinesService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        const string symbol = "BTCUSDT";
        var timeframes = new[] { "1m", "5m", "15m", "1h", "4h", "1d" };

        foreach (var tf in timeframes)
        {
            try
            {
                var limit = tf switch
                {
                    "1m" => 1000,
                    "5m" => 1000,
                    "15m" => 500,
                    "1h" => 500,
                    "4h" => 200,
                    "1d" => 100,
                    _ => 500
                };

                var klines = await binance.GetKlinesAsync(symbol, tf, limit, cancellationToken: cancellationToken);
                if (klines.Count == 0) continue;

                var existingTimes = await db.Klines
                    .AsNoTracking()
                    .Where(k => k.Symbol == symbol && k.Timeframe == tf)
                    .Select(k => k.OpenTimeMs)
                    .ToListAsync(cancellationToken);
                var existingSet = new HashSet<long>(existingTimes);

                var toAdd = new List<Kline>();
                foreach (var k in klines)
                {
                    if (existingSet.Contains(k.OpenTimeMs)) continue;
                    toAdd.Add(new Kline
                    {
                        Symbol = symbol,
                        Timeframe = tf,
                        OpenTimeMs = k.OpenTimeMs,
                        CloseTimeMs = k.CloseTimeMs,
                        Open = k.Open,
                        High = k.High,
                        Low = k.Low,
                        Close = k.Close,
                        Volume = k.Volume,
                        QuoteVolume = k.QuoteVolume,
                        TradeCount = k.TradeCount,
                        TakerBuyVolume = k.TakerBuyVolume,
                        TakerBuyQuoteVolume = k.TakerBuyQuoteVolume
                    });
                }

                if (toAdd.Count > 0)
                {
                    db.Klines.AddRange(toAdd);
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Ingested {Count} new klines for {Symbol} {Timeframe}", toAdd.Count, symbol, tf);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ingest klines for {Symbol} {Timeframe}", symbol, tf);
            }
        }
    }
}
