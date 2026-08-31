using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Backfill dữ liệu nến từ Binance Spot API vào bảng Klines từ 2020-01-01 đến hiện tại.
/// Hỗ trợ chạy idempotent (skip các nến đã tồn tại), rate-limit giữa các request,
/// retry khi lỗi 429/5xx, và log tiến độ rõ ràng.
/// </summary>
public class KlinesBackfillService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<KlinesBackfillService> _logger;
    private readonly TimeProvider _timeProvider;

    // Chỉ cho phép một backfill chạy đồng thởi trên toàn process để tránh
    // duplicate resource usage khi ngườ dùng gọi lại endpoint nhiều lần.
    private static int _isRunning;

    private static readonly DateTime Utc2020 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int BatchLimit = 1000;
    private const int DefaultRequestsPerMinute = 400;

    // Thứ tự ưu tiên: lớn → nhỏ, phù hợp với yêu cầu.
    private static readonly string[] PriorityTimeframes = { "1d", "4h", "1h", "15m", "5m", "1m" };

    public KlinesBackfillService(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<KlinesBackfillService> logger, TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsRunning => Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1;

    /// <summary>
    /// Chạy backfill. Nếu <paramref name="wait"/> = false (mặc định), hàm trả về ngay sau khi
    /// khởi động background work. Nếu <paramref name="wait"/> = true, đợi hoàn thành (có thể rất lâu).
    /// </summary>
    public async Task<BackfillStartInfo> StartAsync(
        string symbol = "BTCUSDT",
        IReadOnlyList<string>? timeframes = null,
        DateTime? startDateUtc = null,
        DateTime? endDateUtc = null,
        int requestsPerMinuteLimit = DefaultRequestsPerMinute,
        bool wait = false,
        bool fillGaps = false,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return new BackfillStartInfo
            {
                Symbol = symbol,
                Timeframes = timeframes?.ToList() ?? PriorityTimeframes.ToList(),
                StartedAtUtc = DateTime.UtcNow,
                Status = "already_running"
            };
        }

        var targetTfs = timeframes?.Count > 0 ? timeframes : PriorityTimeframes;
        var start = startDateUtc ?? Utc2020;
        var end = endDateUtc ?? DateTime.UtcNow;

        var startInfo = new BackfillStartInfo
        {
            Symbol = symbol,
            Timeframes = targetTfs.ToList(),
            StartDateUtc = start,
            EndDateUtc = end,
            StartedAtUtc = DateTime.UtcNow,
            Status = "accepted",
            FillGaps = fillGaps
        };

        // Background work dùng application stopping token thay vì request token,
        // để backfill tiếp tục chạy sau khi HTTP request trả về 202 Accepted.
        var appStoppingToken = _lifetime.ApplicationStopping;
        var workTask = Task.Run(async () =>
        {
            try
            {
                await RunBackfillAsync(symbol, targetTfs, start, end, requestsPerMinuteLimit, fillGaps, appStoppingToken);
            }
            finally
            {
                Interlocked.Exchange(ref _isRunning, 0);
            }
        }, appStoppingToken);

        if (wait)
        {
            await workTask;
            startInfo.Status = "completed";
            startInfo.CompletedAtUtc = DateTime.UtcNow;
        }

        return startInfo;
    }

    private async Task RunBackfillAsync(
        string symbol,
        IReadOnlyList<string> timeframes,
        DateTime startDateUtc,
        DateTime endDateUtc,
        int requestsPerMinuteLimit,
        bool fillGaps,
        CancellationToken cancellationToken)
    {
        var startMs = new DateTimeOffset(startDateUtc).ToUnixTimeMilliseconds();
        var endMs = new DateTimeOffset(endDateUtc).ToUnixTimeMilliseconds();

        _logger.LogInformation(
            "Klines backfill started for {Symbol} from {StartIso} to {EndIso}",
            symbol,
            startDateUtc.ToString("O"),
            endDateUtc.ToString("O"));

        foreach (var tf in timeframes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Backfill cancelled after cancellation token triggered.");
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
                TimeframeBackfillSummary summary;
                if (fillGaps)
                {
                    summary = await BackfillGapsAsync(symbol, tf, intervalMs, startMs, endMs, requestsPerMinuteLimit, cancellationToken);
                    _logger.LogInformation(
                        "Gap-fill finished for {Symbol} {Timeframe}: inserted {Inserted} rows in {RequestCount} requests",
                        symbol, tf, summary.Inserted, summary.RequestCount);
                }
                else
                {
                    summary = await BackfillTimeframeAsync(symbol, tf, intervalMs, startMs, endMs, requestsPerMinuteLimit, cancellationToken);
                    _logger.LogInformation(
                        "Backfill finished for {Symbol} {Timeframe}: inserted {Inserted} rows in {RequestCount} requests",
                        symbol, tf, summary.Inserted, summary.RequestCount);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Backfill cancelled for {Symbol} {Timeframe}", symbol, tf);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backfill failed for {Symbol} {Timeframe}", symbol, tf);
            }
        }

        _logger.LogInformation("Klines backfill finished for {Symbol}", symbol);
    }

    private async Task<TimeframeBackfillSummary> BackfillTimeframeAsync(
        string symbol,
        string timeframe,
        long intervalMs,
        long startMs,
        long endMs,
        int requestsPerMinuteLimit,
        CancellationToken cancellationToken)
    {
        var summary = new TimeframeBackfillSummary { Timeframe = timeframe };
        var delayBetweenRequests = TimeSpan.FromMinutes(1.0 / requestsPerMinuteLimit);

        // Tối ưu resume: nếu đã có dữ liệu trong DB, bắt đầu từ nến cuối cùng + interval
        // thay vì từ 2020 để tránh fetch hàng triệu nến đã tồn tại khi chạy lại.
        // Nếu dữ liệu mới nhất đã vượt quá endMs (ví dụ DB có dữ liệu gần đây nhưng thiếu gap lịch sử),
        // chuyển sang chế độ fill-gap trong khoảng [startMs, endMs].
        var latestExistingMs = await GetLatestOpenTimeMsAsync(symbol, timeframe, cancellationToken);
        long cursorMs = startMs;
        if (latestExistingMs.HasValue && latestExistingMs.Value >= startMs)
        {
            if (latestExistingMs.Value < endMs)
            {
                cursorMs = latestExistingMs.Value + intervalMs;
                _logger.LogInformation(
                    "Backfill resuming for {Symbol} {Timeframe}: latest existing {LatestIso}, cursor {CursorIso}",
                    symbol, timeframe,
                    DateTimeOffset.FromUnixTimeMilliseconds(latestExistingMs.Value).UtcDateTime.ToString("O"),
                    DateTimeOffset.FromUnixTimeMilliseconds(cursorMs).UtcDateTime.ToString("O"));
            }
            else
            {
                cursorMs = startMs;
                _logger.LogInformation(
                    "Backfill gap-fill mode for {Symbol} {Timeframe}: latest existing {LatestIso} is beyond end {EndIso}, filling from {CursorIso}",
                    symbol, timeframe,
                    DateTimeOffset.FromUnixTimeMilliseconds(latestExistingMs.Value).UtcDateTime.ToString("O"),
                    DateTimeOffset.FromUnixTimeMilliseconds(endMs).UtcDateTime.ToString("O"),
                    DateTimeOffset.FromUnixTimeMilliseconds(cursorMs).UtcDateTime.ToString("O"));
            }
        }

        _logger.LogInformation(
            "Backfill starting for {Symbol} {Timeframe}: intervalMs={IntervalMs}, cursor={CursorIso}",
            symbol, timeframe, intervalMs, DateTimeOffset.FromUnixTimeMilliseconds(cursorMs).UtcDateTime.ToString("O"));

        while (cursorMs < endMs && !cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<KlineDto> batch;
            try
            {
                batch = await FetchBatchWithRetryAsync(symbol, timeframe, BatchLimit, cursorMs, endMs, cancellationToken);
                summary.RequestCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backfill aborting for {Symbol} {Timeframe} at cursor {CursorMs}", symbol, timeframe, cursorMs);
                summary.ErrorMessage = ex.Message;
                break;
            }

            if (batch.Count == 0)
            {
                _logger.LogInformation(
                    "Backfill reached empty batch for {Symbol} {Timeframe} at cursor {CursorIso}",
                    symbol, timeframe, DateTimeOffset.FromUnixTimeMilliseconds(cursorMs).UtcDateTime.ToString("O"));
                break;
            }

            var inserted = await InsertBatchAsync(symbol, timeframe, batch, cancellationToken);
            summary.Inserted += inserted;

            var last = batch[^1];
            cursorMs = last.OpenTimeMs + intervalMs;

            _logger.LogInformation(
                "Backfill progress {Symbol} {Timeframe}: batch {BatchSize}, inserted {Inserted}, total {TotalInserted}, cursor {CursorIso}",
                symbol, timeframe, batch.Count, inserted, summary.Inserted,
                DateTimeOffset.FromUnixTimeMilliseconds(cursorMs).UtcDateTime.ToString("O"));

            // Nếu batch < 1000 nghĩa là đã lấy hết dữ liệu trong range [cursor, end]
            if (batch.Count < BatchLimit)
            {
                _logger.LogInformation(
                    "Backfill completed {Symbol} {Timeframe}: final batch size {BatchSize}",
                    symbol, timeframe, batch.Count);
                break;
            }

            try
            {
                await Task.Delay(delayBetweenRequests, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        return summary;
    }

    /// <summary>
    /// Tìm và backfill tất cả các gaps trong khoảng [startMs, endMs] cho một timeframe.
    /// Không dùng logic resume từ nến cuối; thay vào đó quét toàn bộ range để phát hiện nến thiếu.
    /// </summary>
    private async Task<TimeframeBackfillSummary> BackfillGapsAsync(
        string symbol,
        string timeframe,
        long intervalMs,
        long startMs,
        long endMs,
        int requestsPerMinuteLimit,
        CancellationToken cancellationToken)
    {
        var summary = new TimeframeBackfillSummary { Timeframe = timeframe };
        var delayBetweenRequests = TimeSpan.FromMinutes(1.0 / requestsPerMinuteLimit);

        var gaps = await FindGapsAsync(symbol, timeframe, intervalMs, startMs, endMs, cancellationToken);
        if (gaps.Count == 0)
        {
            _logger.LogInformation("No gaps found for {Symbol} {Timeframe} in range {StartIso} to {EndIso}",
                symbol, timeframe,
                DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime.ToString("O"),
                DateTimeOffset.FromUnixTimeMilliseconds(endMs).UtcDateTime.ToString("O"));
            return summary;
        }

        var gapDescriptions = string.Join(", ",
            gaps.Take(10).Select(g => $"{FormatMs(g.StartMs)}-{FormatMs(g.EndMs)}({g.MissingCount})"));
        if (gaps.Count > 10)
            gapDescriptions += $", ... ({gaps.Count - 10} more)";

        _logger.LogInformation(
            "Gap-fill found {GapCount} gaps for {Symbol} {Timeframe}: {Gaps}",
            gaps.Count, symbol, timeframe, gapDescriptions);

        foreach (var gap in gaps)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var cursor = gap.StartMs;
            var gapEnd = Math.Min(gap.EndMs, endMs);
            var gapInserted = 0;
            var gapRequests = 0;

            while (cursor <= gapEnd && !cancellationToken.IsCancellationRequested)
            {
                var estimatedRemaining = (int)Math.Min(BatchLimit, ((gapEnd - cursor) / intervalMs) + 1);
                if (estimatedRemaining <= 0)
                    break;

                IReadOnlyList<KlineDto> batch;
                try
                {
                    batch = await FetchBatchWithRetryAsync(symbol, timeframe, estimatedRemaining, cursor, gapEnd, cancellationToken);
                    summary.RequestCount++;
                    gapRequests++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Gap-fill aborting for {Symbol} {Timeframe} at cursor {CursorMs}", symbol, timeframe, cursor);
                    summary.ErrorMessage = ex.Message;
                    break;
                }

                if (batch.Count == 0)
                {
                    _logger.LogInformation(
                        "Gap-fill reached empty batch for {Symbol} {Timeframe} at cursor {CursorIso}",
                        symbol, timeframe, DateTimeOffset.FromUnixTimeMilliseconds(cursor).UtcDateTime.ToString("O"));
                    break;
                }

                var inserted = await InsertBatchAsync(symbol, timeframe, batch, cancellationToken);
                summary.Inserted += inserted;
                gapInserted += inserted;

                var last = batch[^1];
                cursor = last.OpenTimeMs + intervalMs;

                if (batch.Count < estimatedRemaining)
                    break;

                try
                {
                    await Task.Delay(delayBetweenRequests, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation(
                "Gap-filled {Symbol} {Timeframe} [{StartIso} .. {EndIso}]: inserted {Inserted} in {Requests} request(s)",
                symbol, timeframe,
                DateTimeOffset.FromUnixTimeMilliseconds(gap.StartMs).UtcDateTime.ToString("O"),
                DateTimeOffset.FromUnixTimeMilliseconds(gapEnd).UtcDateTime.ToString("O"),
                gapInserted, gapRequests);
        }

        return summary;
    }

    /// <summary>
    /// Tìm các gaps trong DB cho một timeframe trong khoảng [startMs, endMs].
    /// Sắp xếp theo số nến thiếu giảm dần để ưu tiên gap lớn.
    /// </summary>
    private async Task<IReadOnlyList<Gap>> FindGapsAsync(
        string symbol,
        string timeframe,
        long intervalMs,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        var stats = await GetRangeStatsAsync(symbol, timeframe, startMs, endMs, cancellationToken);
        if (stats is null)
        {
            if (startMs >= endMs)
                return Array.Empty<Gap>();

            var missing = ((endMs - startMs) / intervalMs) + 1;
            return new List<Gap> { new Gap(startMs, endMs, missing) };
        }

        var gaps = new List<Gap>();
        var s = stats.Value;

        // Gap đầu.
        if (s.Min > startMs)
        {
            var missing = (s.Min - startMs) / intervalMs;
            gaps.Add(new Gap(startMs, s.Min - intervalMs, missing));
        }

        // Gap cuối.
        if (s.Max < endMs)
        {
            var missing = (endMs - s.Max) / intervalMs;
            gaps.Add(new Gap(s.Max + intervalMs, endMs, missing));
        }

        // Gap nội bộ.
        var expectedBetweenMinMax = ((s.Max - s.Min) / intervalMs) + 1;
        if (s.Count < expectedBetweenMinMax)
        {
            var existingTimes = await GetOpenTimesAsync(symbol, timeframe, startMs, endMs, cancellationToken);
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
            .ToList();
    }

    private async Task<(long Count, long Min, long Max)?> GetRangeStatsAsync(
        string symbol,
        string timeframe,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var result = await db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.OpenTimeMs >= startMs && k.OpenTimeMs <= endMs)
            .GroupBy(k => 1)
            .Select(g => new { Count = (long)g.Count(), Min = g.Min(k => k.OpenTimeMs), Max = g.Max(k => k.OpenTimeMs) })
            .FirstOrDefaultAsync(cancellationToken);

        if (result is null)
            return null;

        return (result.Count, result.Min, result.Max);
    }

    private async Task<List<long>> GetOpenTimesAsync(
        string symbol,
        string timeframe,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.OpenTimeMs >= startMs && k.OpenTimeMs <= endMs)
            .OrderBy(k => k.OpenTimeMs)
            .Select(k => k.OpenTimeMs)
            .ToListAsync(cancellationToken);
    }

    private static string FormatMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private record Gap(long StartMs, long EndMs, long MissingCount);

    private async Task<IReadOnlyList<KlineDto>> FetchBatchWithRetryAsync(
        string symbol,
        string timeframe,
        int limit,
        long startMs,
        long endMs,
        CancellationToken cancellationToken,
        int maxRetries = 3)
    {
        var attempt = 0;
        while (true)
        {
            using var scope = _scopeFactory.CreateScope();
            var binance = scope.ServiceProvider.GetRequiredService<IBinanceKlinesService>();

            try
            {
                return await binance.GetKlinesAsync(symbol, timeframe, limit, startMs, endMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                var isRateLimit = message.Contains("429", StringComparison.OrdinalIgnoreCase);
                var isServerError = message.Contains("500", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("502", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("503", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("504", StringComparison.OrdinalIgnoreCase);

                if (attempt >= maxRetries)
                {
                    _logger.LogError(ex, "Binance request failed after {MaxRetries} retries for {Symbol} {Timeframe} at {StartMs}", maxRetries, symbol, timeframe, startMs);
                    throw;
                }

                attempt++;
                var backoffSeconds = isRateLimit ? Math.Pow(2, attempt) * 5 : Math.Pow(2, attempt);
                var backoff = TimeSpan.FromSeconds(backoffSeconds);

                _logger.LogWarning(
                    ex,
                    "Binance request failed for {Symbol} {Timeframe} at {StartMs} (rateLimit={IsRateLimit}, serverError={IsServerError}). Retry {Attempt}/{MaxRetries} after {BackoffMs}ms",
                    symbol, timeframe, startMs, isRateLimit, isServerError, attempt, maxRetries, backoff.TotalMilliseconds);

                await Task.Delay(backoff, cancellationToken);
            }
        }
    }

    private async Task<long?> GetLatestOpenTimeMsAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderByDescending(k => k.OpenTimeMs)
            .Select(k => (long?)k.OpenTimeMs)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<int> InsertBatchAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> batch,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCache = scope.ServiceProvider.GetService<DataAuditCache>();

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
            if (existingSet.Contains(k.OpenTimeMs)) continue;

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

        if (toAdd.Count > 0)
        {
            db.Klines.AddRange(toAdd);
            await db.SaveChangesAsync(cancellationToken);
            var minInserted = toAdd.Min(x => x.OpenTimeMs);
            var maxInserted = toAdd.Max(x => x.OpenTimeMs);
            var intervalMs = Timeframes.IntervalToMs(timeframe);
            var affectedGaps = await db.KlineGapStates
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && (x.Status == KlineGapStatuses.Pending || x.Status == KlineGapStatuses.Unavailable)
                    && x.StartOpenTimeMs <= maxInserted && x.EndOpenTimeMs >= minInserted)
                .ToListAsync(cancellationToken);
            foreach (var gap in affectedGaps)
            {
                var expected = ((gap.EndOpenTimeMs - gap.StartOpenTimeMs) / intervalMs) + 1;
                var present = await db.Klines.LongCountAsync(k => k.Symbol == symbol && k.Timeframe == timeframe
                    && k.OpenTimeMs >= gap.StartOpenTimeMs && k.OpenTimeMs <= gap.EndOpenTimeMs, cancellationToken);
                if (present < expected)
                    continue;
                gap.Status = KlineGapStatuses.Filled;
                gap.MissingBars = 0;
                gap.NextRetryAtUtc = null;
                gap.Reason = null;
                gap.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            }
            if (affectedGaps.Count > 0)
                await db.SaveChangesAsync(cancellationToken);
            auditCache?.Invalidate(symbol);
        }

        return toAdd.Count;
    }
}

public class BackfillStartInfo
{
    public string RequestId { get; set; } = string.Empty;
    public string Symbol { get; set; } = "BTCUSDT";
    public List<string> Timeframes { get; set; } = new();
    public DateTime StartDateUtc { get; set; }
    public DateTime EndDateUtc { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "accepted";
    public bool FillGaps { get; set; }
}

public class TimeframeBackfillSummary
{
    public string Timeframe { get; set; } = "";
    public long Inserted { get; set; }
    public int RequestCount { get; set; }
    public string? ErrorMessage { get; set; }
}
