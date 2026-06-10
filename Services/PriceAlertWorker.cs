using Backend.Data;
using Backend.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Periodically compares BTC last close from Binance to per-user thresholds in DB and persists alerts (with cooldown).
/// </summary>
public class PriceAlertWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AlertOptions> _optionsMonitor;
    private readonly ILogger<PriceAlertWorker> _logger;

    public PriceAlertWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AlertOptions> optionsMonitor,
        ILogger<PriceAlertWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _optionsMonitor.CurrentValue;

            if (!opts.WorkerEnabled)
            {
                await DelayPoll(opts, stoppingToken);
                continue;
            }

            try
            {
                await RunCycleAsync(opts, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Price alert cycle failed");
            }

            await DelayPoll(opts, stoppingToken);
        }
    }

    private async Task DelayPoll(AlertOptions opts, CancellationToken stoppingToken)
    {
        var seconds = Math.Clamp(opts.PollSeconds, 10, 3600);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // shutdown
        }
    }

    private async Task RunCycleAsync(AlertOptions opts, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var binance = scope.ServiceProvider.GetRequiredService<IBinanceKlinesService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seqEngine = scope.ServiceProvider.GetRequiredService<ICandleSequenceRulesEngine>();

        var userId = string.IsNullOrWhiteSpace(opts.DefaultUserId) ? "default" : opts.DefaultUserId.Trim();

        var settings = await db.PriceAlertSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

        if (settings == null)
        {
            _logger.LogWarning("Price alert: no PriceAlertSettings row for user {UserId}", userId);
            return;
        }

        if (!settings.Enabled)
            return;

        var interval = string.IsNullOrWhiteSpace(settings.KlineInterval) ? "1m" : settings.KlineInterval.Trim();
        var cooldown = Math.Max(1, settings.CooldownMinutes);

        // --- Classic price alerts ---
        if (settings.PriceAboveUsd.HasValue || settings.PriceBelowUsd.HasValue)
        {
            var priceKlines = await binance.GetBtcKlinesAsync(interval, limit: 1, cancellationToken);
            if (priceKlines.Count > 0)
            {
                var close = priceKlines[^1].Close;
                if (settings.PriceAboveUsd.HasValue && close > settings.PriceAboveUsd.Value)
                {
                    await TryCreateAlertAsync(db, userId, "price_above", "BTC vượt ngưỡng giá",
                        $"Giá đóng nến ({interval}) {close:F2} USDT > {settings.PriceAboveUsd.Value:F2} USDT.", close, cooldown, cancellationToken);
                }
                if (settings.PriceBelowUsd.HasValue && close < settings.PriceBelowUsd.Value)
                {
                    await TryCreateAlertAsync(db, userId, "price_below", "BTC dưới ngưỡng giá",
                        $"Giá đóng nến ({interval}) {close:F2} USDT < {settings.PriceBelowUsd.Value:F2} USDT.", close, cooldown, cancellationToken);
                }
            }
        }

        // --- Candle Sequence Rules evaluation ---
        try
        {
            var timeframes = await db.CandleSequenceRules
                .AsNoTracking()
                .Where(r => r.IsEnabled && r.Symbol == "BTCUSDT")
                .Select(r => r.Timeframe)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var tf in timeframes)
            {
                var maxBars = await db.CandleSequenceRules
                    .AsNoTracking()
                    .Where(r => r.IsEnabled && r.Symbol == "BTCUSDT" && r.Timeframe == tf)
                    .Select(r => (int?)r.RequiredBars)
                    .MaxAsync(cancellationToken) ?? 10;

                var limit = Math.Max(50, maxBars);
                var klines = await binance.GetBtcKlinesAsync(tf, limit: limit, cancellationToken);
                if (klines.Count == 0) continue;

                var signals = await seqEngine.EvaluateAsync("BTCUSDT", tf, klines, cancellationToken);
                foreach (var signal in signals)
                {
                    await TryCreateAlertAsync(db, userId, "sequence_rule", signal.RuleName, signal.Message, signal.TriggerClose, cooldown, cancellationToken);

                    db.CandleSequenceSignals.Add(new CandleSequenceSignal
                    {
                        RuleId = signal.RuleId,
                        Symbol = signal.Symbol,
                        Timeframe = signal.Timeframe,
                        TriggerTimeMs = signal.TriggerTimeMs,
                        ClosePrice = signal.TriggerClose,
                        Message = signal.Message,
                        CreatedAtUtc = DateTime.UtcNow
                    });
                }

                if (signals.Count > 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Sequence rules triggered {Count} signals for {Interval}", signals.Count, tf);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sequence rules evaluation failed");
        }
    }

    private static async Task TryCreateAlertAsync(
        AppDbContext db,
        string userId,
        string type,
        string title,
        string message,
        decimal priceSnapshot,
        int cooldownMinutes,
        CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-cooldownMinutes);
        var recent = await db.AppAlerts.AnyAsync(
            a => a.UserId == userId && a.Type == type && a.CreatedAt >= since,
            cancellationToken);

        if (recent)
            return;

        db.AppAlerts.Add(new AppAlert
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Title = title,
            Message = message,
            PriceSnapshot = priceSnapshot,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRead = false
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
