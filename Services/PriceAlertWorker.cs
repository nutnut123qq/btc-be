using Backend.Data;
using Backend.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

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
        var telegram = scope.ServiceProvider.GetService<ITelegramNotificationService>();

        var userId = string.IsNullOrWhiteSpace(opts.DefaultUserId) ? "default" : opts.DefaultUserId.Trim();

        await ArchiveExpiredReadAlertsAsync(db, opts.ReadRetentionDays, cancellationToken);

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
                    var sourceKey = BuildPriceSourceKey(userId, "above", settings.PriceAboveUsd.Value, interval, priceKlines[^1].OpenTimeMs);
                    await TryCreateAlertAsync(db, telegram, userId, "price_above", "BTC vượt ngưỡng giá",
                        $"Giá đóng nến ({interval}) {close:F2} USDT > {settings.PriceAboveUsd.Value:F2} USDT.", close, sourceKey, cooldown, cancellationToken);
                }
                if (settings.PriceBelowUsd.HasValue && close < settings.PriceBelowUsd.Value)
                {
                    var sourceKey = BuildPriceSourceKey(userId, "below", settings.PriceBelowUsd.Value, interval, priceKlines[^1].OpenTimeMs);
                    await TryCreateAlertAsync(db, telegram, userId, "price_below", "BTC dưới ngưỡng giá",
                        $"Giá đóng nến ({interval}) {close:F2} USDT < {settings.PriceBelowUsd.Value:F2} USDT.", close, sourceKey, cooldown, cancellationToken);
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
                    var sourceKey = BuildSequenceSourceKey(userId, signal.RuleId, signal.Symbol, signal.Timeframe, signal.TriggerTimeMs);
                    var created = await TryCreateAlertAsync(db, telegram, userId, "sequence_rule", signal.RuleName, signal.Message, signal.TriggerClose, sourceKey, cooldown, cancellationToken);
                    if (!created) continue;

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

    internal static string BuildSequenceSourceKey(string userId, long ruleId, string symbol, string timeframe, long candleOpenTimeMs) =>
        $"sequence:{userId.Trim()}:{ruleId}:{symbol.Trim().ToUpperInvariant()}:{timeframe.Trim().ToLowerInvariant()}:{candleOpenTimeMs}";

    internal static string BuildPriceSourceKey(string userId, string direction, decimal threshold, string timeframe, long candleOpenTimeMs) =>
        $"price:{userId.Trim()}:{direction.Trim().ToLowerInvariant()}:{threshold.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)}:{timeframe.Trim().ToLowerInvariant()}:{candleOpenTimeMs}";

    private static async Task ArchiveExpiredReadAlertsAsync(
        AppDbContext db,
        int retentionDays,
        CancellationToken cancellationToken)
    {
        if (retentionDays <= 0) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        var expired = await db.AppAlerts
            .Where(a => a.IsRead && a.ArchivedAtUtc == null && a.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0) return;
        var archivedAt = DateTime.UtcNow;
        foreach (var alert in expired) alert.ArchivedAtUtc = archivedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static async Task<bool> TryCreateAlertAsync(
        AppDbContext db,
        ITelegramNotificationService? telegram,
        string userId,
        string type,
        string title,
        string message,
        decimal priceSnapshot,
        string sourceKey,
        int cooldownMinutes,
        CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, cooldownMinutes));
        var recent = await db.AppAlerts.AnyAsync(
            a => a.UserId == userId
                && (a.SourceKey == sourceKey
                    || (a.Type == type && a.ArchivedAtUtc == null && a.CreatedAt >= since)),
            cancellationToken);

        if (recent)
            return false;

        var alert = new AppAlert
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Title = title,
            Message = message,
            PriceSnapshot = priceSnapshot,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRead = false,
            SourceKey = sourceKey
        };
        db.AppAlerts.Add(alert);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(alert).State = EntityState.Detached;
            return false;
        }

        if (telegram != null)
        {
            var encTitle = System.Net.WebUtility.HtmlEncode(alert.Title);
            var encMsg = System.Net.WebUtility.HtmlEncode(alert.Message);
            var tgMsg = $"🔔 <b>Cảnh báo BTC</b>\n📍 Giá: ${alert.PriceSnapshot:N0}\n⚡ {encTitle}\n📝 {encMsg}";
            await telegram.SendMessageAsync(tgMsg, cancellationToken);
        }

        return true;
    }
}
