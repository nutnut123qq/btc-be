using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Tự động ingest klines từ Binance vào DB để phục vụ train AI & backtest.
/// Mỗi chu kỳ worker:
///   1. Lấy dữ liệu mới nhất cho tất cả timeframe.
///   2. Quét khoảng thờ gian từ <see cref="KlinesIngestionOptions.BackfillStartDate"/> đến hiện tại,
///      phát hiện các nến bị thiếu (gaps).
///   3. Backfill các gaps bằng cách gọi Binance API theo startTime/endTime,
///      tuân thủ giới hạn <see cref="KlinesIngestionOptions.MaxRequestsPerCycle"/>.
/// </summary>
public class KlinesIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KlinesIngestionWorker> _logger;
    private readonly KlinesIngestionOptions _options;

    private static readonly string[] DefaultSymbols = { "BTCUSDT", "ETHUSDT", "SOLUSDT" };
    private const int BatchLimit = 1000;

    // Ưu tiên khung lớn trước: ít request hơn, giảm gapCount nhanh hơn.
    private static readonly string[] DefaultTimeframes = { "1d", "4h", "1h", "30m", "15m", "5m", "1m" };

    public KlinesIngestionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<KlinesIngestionWorker> logger,
        IOptions<KlinesIngestionOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
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

        var startMs = ToUtcMs(_options.BackfillStartDate);
        var endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var maxRequests = Math.Max(1, _options.MaxRequestsPerCycle);
        var remainingRequests = maxRequests;
        var totalInserted = 0;

        _logger.LogInformation(
            "Klines ingestion cycle started for {SymbolsCount} symbols. Budget={Budget} requests, range={StartIso} to {EndIso}",
            DefaultSymbols.Length,
            maxRequests,
            DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime.ToString("O"),
            DateTimeOffset.FromUnixTimeMilliseconds(endMs).UtcDateTime.ToString("O"));

        foreach (var symbol in DefaultSymbols)
        {
            if (remainingRequests <= 0 || cancellationToken.IsCancellationRequested)
                break;

            foreach (var tf in DefaultTimeframes)
            {
                if (remainingRequests <= 0 || cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Klines ingestion request budget exhausted; skipping remaining symbols/timeframes");
                    break;
                }

                var intervalMs = Timeframes.IntervalToMs(tf);
                if (intervalMs <= 0)
                {
                    _logger.LogWarning("Skipping invalid timeframe {Timeframe}", tf);
                    continue;
                }

                try
                {
                    // 1. Lấy dữ liệu mới nhất.
                    var latestLimit = GetLatestLimit(tf);
                    var latest = await binance.GetKlinesAsync(symbol, tf, latestLimit, cancellationToken: cancellationToken);
                    remainingRequests--;

                    var latestInserted = await InsertBatchAsync(db, symbol, tf, latest, cancellationToken);
                    totalInserted += latestInserted;

                    _logger.LogInformation(
                        "Fetched latest {Count} klines for {Symbol} {Timeframe}, inserted {Inserted}. Budget remaining {Remaining}",
                        latest.Count, symbol, tf, latestInserted, remainingRequests);

                    // 2. Phát hiện gaps trong khoảng đã cấu hình.
                    var gaps = await FindGapsAsync(db, symbol, tf, startMs, endMs, _options.MaxGapsPerTimeframe, cancellationToken);
                    if (gaps.Count == 0)
                    {
                        _logger.LogInformation("No gaps detected for {Symbol} {Timeframe}", symbol, tf);
                        continue;
                    }

                    var gapDescriptions = string.Join(", ",
                        gaps.Take(10).Select(g => $"{FormatMs(g.StartMs)}-{FormatMs(g.EndMs)}({g.MissingCount})"));

                    if (gaps.Count > 10)
                        gapDescriptions += $", ... ({gaps.Count - 10} more)";

                    _logger.LogInformation(
                        "Detected {GapCount} gaps for {Symbol} {Timeframe}: {Gaps}",
                        gaps.Count, symbol, tf, gapDescriptions);

                    // 3. Backfill gaps cho đến khi hết budget.
                    foreach (var gap in gaps)
                    {
                        if (remainingRequests <= 0 || cancellationToken.IsCancellationRequested)
                            break;

                        var (inserted, requestsUsed) = await BackfillGapAsync(
                            binance, db, symbol, tf, intervalMs,
                            gap.StartMs, Math.Min(gap.EndMs, endMs),
                            remainingRequests, cancellationToken);

                        remainingRequests -= requestsUsed;
                        totalInserted += inserted;

                        _logger.LogInformation(
                            "Backfilled gap {Symbol} {Timeframe} [{StartIso} .. {EndIso}]: inserted {Inserted} in {RequestsUsed} request(s), budget remaining {Remaining}",
                            symbol, tf,
                            DateTimeOffset.FromUnixTimeMilliseconds(gap.StartMs).UtcDateTime.ToString("O"),
                            DateTimeOffset.FromUnixTimeMilliseconds(gap.EndMs).UtcDateTime.ToString("O"),
                            inserted, requestsUsed, remainingRequests);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process klines for {Symbol} {Timeframe}", symbol, tf);
                }
            }
        }

        _logger.LogInformation(
            "Klines ingestion cycle completed. Total inserted {TotalInserted}, budget remaining {Remaining}/{Budget}",
            totalInserted, remainingRequests, maxRequests);
    }

    /// <summary>
    /// Backfill một khoảng gap từ <paramref name="gapStartMs"/> đến <paramref name="gapEndMs"/>.
    /// Trả về số row đã insert và số request đã dùng.
    /// </summary>
    internal async Task<(int Inserted, int RequestsUsed)> BackfillGapAsync(
        IBinanceKlinesService binance,
        AppDbContext db,
        string symbol,
        string timeframe,
        long intervalMs,
        long gapStartMs,
        long gapEndMs,
        int requestBudget,
        CancellationToken cancellationToken)
    {
        if (gapStartMs > gapEndMs || requestBudget <= 0)
            return (0, 0);

        var inserted = 0;
        var requestsUsed = 0;
        var cursor = gapStartMs;
        var delay = ComputeRequestDelay();

        while (cursor <= gapEndMs && requestsUsed < requestBudget && !cancellationToken.IsCancellationRequested)
        {
            var estimatedRemaining = (int)Math.Min(BatchLimit, ((gapEndMs - cursor) / intervalMs) + 1);
            if (estimatedRemaining <= 0)
                break;

            IReadOnlyList<KlineDto> batch;
            try
            {
                batch = await binance.GetKlinesAsync(symbol, timeframe, estimatedRemaining, cursor, gapEndMs, cancellationToken);
                requestsUsed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Binance request failed for {Symbol} {Timeframe} gap at cursor {CursorIso}",
                    symbol, timeframe,
                    DateTimeOffset.FromUnixTimeMilliseconds(cursor).UtcDateTime.ToString("O"));
                break;
            }

            if (batch.Count == 0)
            {
                _logger.LogInformation(
                    "Empty batch for {Symbol} {Timeframe} gap at cursor {CursorIso}; stopping gap backfill",
                    symbol, timeframe,
                    DateTimeOffset.FromUnixTimeMilliseconds(cursor).UtcDateTime.ToString("O"));
                break;
            }

            var batchInserted = await InsertBatchAsync(db, symbol, timeframe, batch, cancellationToken);
            inserted += batchInserted;

            var last = batch[^1];
            cursor = last.OpenTimeMs + intervalMs;

            // Nếu batch nhỏ hơn limit thì đã lấy hết dữ liệu trong gap.
            if (batch.Count < estimatedRemaining)
                break;

            // Chờ giữa các request để tránh rate limit, trừ request cuối cùng trong budget.
            if (requestsUsed < requestBudget)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        return (inserted, requestsUsed);
    }

    /// <summary>
    /// Tìm các gaps trong DB cho một timeframe trong khoảng [startMs, endMs].
    /// Trả về danh sách gaps sắp xếp theo số nến thiếu giảm dần.
    /// </summary>
    internal async Task<IReadOnlyList<Gap>> FindGapsAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        long startMs,
        long endMs,
        int maxGaps,
        CancellationToken cancellationToken)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0)
            return Array.Empty<Gap>();

        var stats = await db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.OpenTimeMs >= startMs && k.OpenTimeMs <= endMs)
            .GroupBy(k => 1)
            .Select(g => new { Count = g.Count(), Min = g.Min(k => k.OpenTimeMs), Max = g.Max(k => k.OpenTimeMs) })
            .FirstOrDefaultAsync(cancellationToken);

        if (stats is null)
        {
            if (startMs >= endMs)
                return Array.Empty<Gap>();

            var missing = ((endMs - startMs) / intervalMs) + 1;
            return new List<Gap> { new(startMs, endMs, missing) };
        }

        var gaps = new List<Gap>();

        // Gap đầu: từ startMs đến nến đầu tiên trong DB.
        if (stats.Min > startMs)
        {
            var missing = (stats.Min - startMs) / intervalMs;
            gaps.Add(new Gap(startMs, stats.Min - intervalMs, missing));
        }

        // Gap cuối: từ nến cuối trong DB đến endMs.
        if (stats.Max < endMs)
        {
            var missing = (endMs - stats.Max) / intervalMs;
            gaps.Add(new Gap(stats.Max + intervalMs, endMs, missing));
        }

        // Gap nội bộ: chỉ khi số lượng nến thực tế ít hơn kỳ vọng giữa Min và Max.
        var expectedBetweenMinMax = ((stats.Max - stats.Min) / intervalMs) + 1;
        if (stats.Count < expectedBetweenMinMax)
        {
            var existingTimes = await db.Klines
                .AsNoTracking()
                .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.OpenTimeMs >= startMs && k.OpenTimeMs <= endMs)
                .OrderBy(k => k.OpenTimeMs)
                .Select(k => k.OpenTimeMs)
                .ToListAsync(cancellationToken);

            for (var i = 1; i < existingTimes.Count; i++)
            {
                var prev = existingTimes[i - 1];
                var curr = existingTimes[i];
                if (curr - prev > intervalMs)
                {
                    var missing = ((curr - prev) / intervalMs) - 1;
                    gaps.Add(new Gap(prev + intervalMs, curr - intervalMs, missing));
                }
            }
        }

        return gaps
            .OrderByDescending(g => g.MissingCount)
            .Take(maxGaps)
            .ToList();
    }

    /// <summary>Insert một batch nến, bỏ qua các nến đã tồn tại. Xử lý lỗi duplicate key.</summary>
    internal async Task<int> InsertBatchAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return 0;

        var openTimes = batch.Select(x => x.OpenTimeMs).ToList();
        var existing = await db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && openTimes.Contains(k.OpenTimeMs))
            .Select(k => k.OpenTimeMs)
            .ToListAsync(cancellationToken);
        var existingSet = new HashSet<long>(existing);

        var toAdd = new List<Kline>(batch.Count);
        foreach (var k in batch)
        {
            if (existingSet.Contains(k.OpenTimeMs))
                continue;

            toAdd.Add(new Kline
            {
                Symbol = symbol,
                Timeframe = timeframe,
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

        if (toAdd.Count == 0)
            return 0;

        db.Klines.AddRange(toAdd);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return toAdd.Count;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(
                ex,
                "Duplicate key conflict while inserting klines for {Symbol} {Timeframe}; skipping batch",
                symbol, timeframe);
            return 0;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static int GetLatestLimit(string timeframe) => timeframe switch
    {
        "1m" => 1000,
        "5m" => 1000,
        "15m" => 500,
        "1h" => 500,
        "4h" => 200,
        "1d" => 100,
        _ => 500
    };

    private TimeSpan ComputeRequestDelay()
    {
        // Phân bổ đều các request trong vòng một phút, nhưng không chờ quá lâu.
        var delayMs = Math.Max(100, (int)(60_000.0 / Math.Max(1, _options.MaxRequestsPerCycle)));
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private static long ToUtcMs(DateTime value)
    {
        var normalized = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value;
        return new DateTimeOffset(normalized).ToUnixTimeMilliseconds();
    }

    private static string FormatMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>Đại diện một khoảng thờ gian bị thiếu nến.</summary>
    internal record Gap(long StartMs, long EndMs, long MissingCount);
}
