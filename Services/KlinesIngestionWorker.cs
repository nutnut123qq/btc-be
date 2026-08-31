using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

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
    private readonly DataAuditCache? _cache;
    private readonly TimeProvider _timeProvider;

    private static readonly string[] DefaultSymbols = { "BTCUSDT" };
    private const int BatchLimit = 1000;

    // Ưu tiên khung lớn trước: ít request hơn, giảm gapCount nhanh hơn.
    private static readonly string[] DefaultTimeframes = { "1d", "4h", "1h", "30m", "15m", "5m", "1m" };

    public KlinesIngestionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<KlinesIngestionWorker> logger,
        IOptions<KlinesIngestionOptions> options,
        DataAuditCache? cache = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _cache = cache;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Klines ingestion cycle failed");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await WorkerHeartbeatStore.MarkFailedAsync(
                        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                        nameof(KlinesIngestionWorker), startedAtUtc, _timeProvider.GetUtcNow().UtcDateTime, ex, stoppingToken);
                }
                catch (Exception heartbeatException)
                {
                    _logger.LogWarning(heartbeatException, "Could not persist failed ingestion heartbeat");
                }
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

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var binance = scope.ServiceProvider.GetRequiredService<IBinanceKlinesService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cycleStartedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await WorkerHeartbeatStore.MarkStartedAsync(db, nameof(KlinesIngestionWorker), cycleStartedAt, cancellationToken);
        var startMs = ToUtcMs(_options.BackfillStartDate);
        var endMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var maxRequests = Math.Max(1, _options.MaxRequestsPerCycle);
        var remainingRequests = maxRequests;
        var maxRequestsPerTimeframe = Math.Max(1,
            (maxRequests + DefaultTimeframes.Length - 1) / DefaultTimeframes.Length);
        var totalInserted = 0;
        Exception? firstFailure = null;

        _logger.LogInformation(
            "Klines ingestion cycle started for {SymbolsCount} symbols. Historical budget={Budget} requests, range={StartIso} to {EndIso}",
            DefaultSymbols.Length,
            maxRequests,
            DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime.ToString("O"),
            DateTimeOffset.FromUnixTimeMilliseconds(endMs).UtcDateTime.ToString("O"));

        // Latest ingestion has its own budget: historical gaps can never starve current candles.
        foreach (var symbol in DefaultSymbols)
        {
            foreach (var tf in DefaultTimeframes)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                try
                {
                    var latestLimit = GetLatestLimit(tf);
                    var latest = await binance.GetKlinesAsync(symbol, tf, latestLimit, cancellationToken: cancellationToken);
                    var latestInserted = await InsertBatchAsync(db, symbol, tf, latest, cancellationToken);
                    totalInserted += latestInserted;
                    _logger.LogInformation(
                        "Fetched latest {Count} klines for {Symbol} {Timeframe}, inserted {Inserted}",
                        latest.Count, symbol, tf, latestInserted);
                }
                catch (Exception ex)
                {
                    firstFailure ??= ex;
                    _logger.LogWarning(ex, "Failed latest klines for {Symbol} {Timeframe}", symbol, tf);
                }
            }
        }

        // Discover every timeframe before spending the separate historical budget.
        foreach (var symbol in DefaultSymbols)
        {
            foreach (var tf in DefaultTimeframes)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                try
                {
                    var gaps = await FindGapsAsync(db, symbol, tf, startMs, endMs, _options.MaxGapsPerTimeframe, cancellationToken);
                    await PersistDetectedGapsAsync(db, symbol, tf, gaps, cancellationToken);
                    await ReconcileResolvedGapStatesAsync(db, symbol, tf, Timeframes.IntervalToMs(tf), cancellationToken);
                }
                catch (Exception ex)
                {
                    firstFailure ??= ex;
                    _logger.LogWarning(ex, "Failed historical gaps for {Symbol} {Timeframe}", symbol, tf);
                }
            }
        }

        var due = await GetDueGapStatesAsync(db, _options.MaxGapsPerTimeframe * DefaultTimeframes.Length, cancellationToken);
        var usedByTimeframe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in due)
        {
            var used = usedByTimeframe.GetValueOrDefault(state.Timeframe);
            if (remainingRequests <= 0 || cancellationToken.IsCancellationRequested)
                break;
            if (used >= maxRequestsPerTimeframe)
                continue;
            try
            {
                var intervalMs = Timeframes.IntervalToMs(state.Timeframe);
                var (inserted, requestsUsed, emptyResponse, requestFailed) = await BackfillGapAsync(
                    binance, db, state.Symbol, state.Timeframe, intervalMs,
                    state.StartOpenTimeMs, Math.Min(state.EndOpenTimeMs, endMs),
                    Math.Min(remainingRequests, maxRequestsPerTimeframe - used), cancellationToken);
                remainingRequests -= requestsUsed;
                usedByTimeframe[state.Timeframe] = used + requestsUsed;
                totalInserted += inserted;
                if (requestFailed)
                    firstFailure ??= new HttpRequestException("Historical Binance request failed; see backend logs.");
                await UpdateGapAfterAttemptAsync(db, state.Id, intervalMs, emptyResponse,
                    cancellationToken, requestFailed ? "Binance request failed" : null);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                _logger.LogWarning(ex, "Failed historical gap {GapId}", state.Id);
            }
        }

        if (firstFailure is null)
            await WorkerHeartbeatStore.MarkSucceededAsync(db, nameof(KlinesIngestionWorker), cycleStartedAt,
                _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        else
            await WorkerHeartbeatStore.MarkFailedAsync(db, nameof(KlinesIngestionWorker), cycleStartedAt,
                _timeProvider.GetUtcNow().UtcDateTime, firstFailure, cancellationToken);
        _logger.LogInformation(
            "Klines ingestion cycle completed. Total inserted {TotalInserted}, historical budget remaining {Remaining}/{Budget}",
            totalInserted, remainingRequests, maxRequests);
    }

    internal Task<List<KlineGapState>> GetDueGapStatesAsync(
        AppDbContext db, int take, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return db.KlineGapStates
            .Where(x => x.Status == KlineGapStatuses.Pending
                && (x.NextRetryAtUtc == null || x.NextRetryAtUtc <= now))
            .OrderBy(x => x.LastAttemptAtUtc.HasValue)
            .ThenBy(x => x.LastAttemptAtUtc)
            .ThenBy(x => x.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Backfill một khoảng gap từ <paramref name="gapStartMs"/> đến <paramref name="gapEndMs"/>.
    /// Trả về số row đã insert và số request đã dùng.
    /// </summary>
    internal async Task<(int Inserted, int RequestsUsed, bool EmptyResponse, bool RequestFailed)> BackfillGapAsync(
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
            return (0, 0, false, false);

        var inserted = 0;
        var requestsUsed = 0;
        var emptyResponse = false;
        var requestFailed = false;
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
                requestsUsed++;
                requestFailed = true;
                _logger.LogWarning(
                    ex,
                    "Binance request failed for {Symbol} {Timeframe} gap at cursor {CursorIso}",
                    symbol, timeframe,
                    DateTimeOffset.FromUnixTimeMilliseconds(cursor).UtcDateTime.ToString("O"));
                break;
            }

            if (batch.Count == 0)
            {
                emptyResponse = true;
                _logger.LogInformation(
                    "Empty batch for {Symbol} {Timeframe} gap at cursor {CursorIso}; stopping this backfill attempt",
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

        return (inserted, requestsUsed, emptyResponse, requestFailed);
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
        if (intervalMs <= 0 || startMs > endMs)
            return Array.Empty<Gap>();
        endMs -= endMs % intervalMs;

        var stats = await db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.OpenTimeMs >= startMs && k.OpenTimeMs <= endMs)
            .GroupBy(k => 1)
            .Select(g => new { Count = g.Count(), Min = g.Min(k => k.OpenTimeMs), Max = g.Max(k => k.OpenTimeMs) })
            .FirstOrDefaultAsync(cancellationToken);

        if (stats is null)
        {
            var missing = ((endMs - startMs) / intervalMs) + 1;
            return new List<Gap> { new(startMs, endMs, missing) };
        }

        var gaps = new List<Gap>();

        // Gap đầu: từ startMs đến nến đầu tiên trong DB.
        if (stats.Min - startMs >= intervalMs)
        {
            var missing = (stats.Min - startMs) / intervalMs;
            gaps.Add(new Gap(startMs, stats.Min - intervalMs, missing));
        }

        // Gap cuối: từ nến cuối trong DB đến endMs.
        if (endMs - stats.Max >= intervalMs)
        {
            var missing = (endMs - stats.Max) / intervalMs;
            gaps.Add(new Gap(stats.Max + intervalMs, endMs, missing));
        }

        // Gap nội bộ: chỉ khi số lượng nến thực tế ít hơn kỳ vọng giữa Min và Max.
        var expectedBetweenMinMax = ((stats.Max - stats.Min) / intervalMs) + 1;
        if (stats.Count < expectedBetweenMinMax)
        {
            var internalGaps = await KlineGapQuery.GetTopInternalGapsAsync(
                db, symbol, timeframe, intervalMs, maxGaps, startMs, endMs, cancellationToken);
            gaps.AddRange(internalGaps.Select(x => new Gap(x.StartOpenTimeMs, x.EndOpenTimeMs, x.MissingBars)));
        }

        return gaps
            .OrderByDescending(g => g.MissingCount)
            .Take(maxGaps)
            .ToList();
    }

    internal async Task PersistDetectedGapsAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        IReadOnlyList<Gap> gaps,
        CancellationToken cancellationToken)
    {
        if (gaps.Count == 0)
            return;

        var minStart = gaps.Min(x => x.StartMs);
        var maxEnd = gaps.Max(x => x.EndMs);
        var existing = await db.KlineGapStates
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.StartOpenTimeMs <= maxEnd && x.EndOpenTimeMs >= minStart)
            .ToListAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var assignedMissing = new Dictionary<KlineGapState, long>();
        foreach (var gap in gaps)
        {
            var state = existing
                .Where(x => x.StartOpenTimeMs <= gap.StartMs && x.EndOpenTimeMs >= gap.EndMs)
                .OrderBy(x => x.EndOpenTimeMs - x.StartOpenTimeMs)
                .FirstOrDefault()
                ?? existing.Where(x => x.StartOpenTimeMs <= gap.EndMs && x.EndOpenTimeMs >= gap.StartMs)
                    .OrderBy(x => x.Id)
                    .FirstOrDefault();
            if (state is not null)
            {
                if (state.Status == KlineGapStatuses.Filled)
                {
                    state.Status = KlineGapStatuses.Pending;
                    state.AttemptCount = 0;
                    state.NextRetryAtUtc = null;
                    state.Reason = "Previously filled gap was detected again.";
                }
                state.StartOpenTimeMs = Math.Min(state.StartOpenTimeMs, gap.StartMs);
                state.EndOpenTimeMs = Math.Max(state.EndOpenTimeMs, gap.EndMs);
                assignedMissing[state] = assignedMissing.GetValueOrDefault(state) + gap.MissingCount;
                state.MissingBars = assignedMissing[state];
                state.UpdatedAtUtc = now;
                continue;
            }
            var added = new KlineGapState
            {
                Symbol = symbol,
                Timeframe = timeframe,
                StartOpenTimeMs = gap.StartMs,
                EndOpenTimeMs = gap.EndMs,
                MissingBars = gap.MissingCount,
                Status = KlineGapStatuses.Pending,
                FirstDetectedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.KlineGapStates.Add(added);
            existing.Add(added);
            assignedMissing[added] = gap.MissingCount;
        }
        await db.SaveChangesAsync(cancellationToken);
        _cache?.Invalidate(symbol);
    }

    internal async Task<int> ReconcileResolvedGapStatesAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        long intervalMs,
        CancellationToken cancellationToken)
    {
        if (intervalMs <= 0)
            return 0;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        int updated;
        if (db.Database.IsRelational())
        {
            updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "KlineGapStates" AS g
                SET "Status" = {KlineGapStatuses.Filled}, "MissingBars" = 0,
                    "NextRetryAtUtc" = NULL, "Reason" = NULL, "UpdatedAtUtc" = {now}
                WHERE g."Symbol" = {symbol} AND g."Timeframe" = {timeframe}
                  AND g."Status" IN ({KlineGapStatuses.Pending}, {KlineGapStatuses.Unavailable})
                  AND (SELECT COUNT(*) FROM "Klines" AS k
                       WHERE k."Symbol" = g."Symbol" AND k."Timeframe" = g."Timeframe"
                         AND k."OpenTimeMs" >= g."StartOpenTimeMs"
                         AND k."OpenTimeMs" <= g."EndOpenTimeMs")
                      >= ((g."EndOpenTimeMs" - g."StartOpenTimeMs") / {intervalMs}) + 1
                """, cancellationToken);
        }
        else
        {
            updated = 0;
            var states = await db.KlineGapStates
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && (x.Status == KlineGapStatuses.Pending || x.Status == KlineGapStatuses.Unavailable))
                .ToListAsync(cancellationToken);
            foreach (var state in states)
            {
                var expected = ((state.EndOpenTimeMs - state.StartOpenTimeMs) / intervalMs) + 1;
                var present = await db.Klines.LongCountAsync(k => k.Symbol == symbol && k.Timeframe == timeframe
                    && k.OpenTimeMs >= state.StartOpenTimeMs && k.OpenTimeMs <= state.EndOpenTimeMs, cancellationToken);
                if (present < expected)
                    continue;
                state.Status = KlineGapStatuses.Filled;
                state.MissingBars = 0;
                state.NextRetryAtUtc = null;
                state.Reason = null;
                state.UpdatedAtUtc = now;
                updated++;
            }
            if (updated > 0)
                await db.SaveChangesAsync(cancellationToken);
        }
        if (updated > 0)
            _cache?.Invalidate(symbol);
        return updated;
    }

    internal async Task UpdateGapAfterAttemptAsync(
        AppDbContext db,
        long gapStateId,
        long intervalMs,
        bool emptyResponse,
        CancellationToken cancellationToken,
        string? failureReason = null)
    {
        var state = await db.KlineGapStates.SingleAsync(x => x.Id == gapStateId, cancellationToken);
        var expected = ((state.EndOpenTimeMs - state.StartOpenTimeMs) / intervalMs) + 1;
        var present = await db.Klines.AsNoTracking().LongCountAsync(k =>
            k.Symbol == state.Symbol && k.Timeframe == state.Timeframe
            && k.OpenTimeMs >= state.StartOpenTimeMs && k.OpenTimeMs <= state.EndOpenTimeMs,
            cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        state.LastAttemptAtUtc = now;
        state.UpdatedAtUtc = now;
        state.MissingBars = Math.Max(0, expected - present);
        if (state.MissingBars == 0)
        {
            state.Status = KlineGapStatuses.Filled;
            state.NextRetryAtUtc = null;
            state.Reason = null;
        }
        else if (emptyResponse)
        {
            state.AttemptCount++;
            if (state.AttemptCount >= 3)
            {
                state.Status = KlineGapStatuses.Unavailable;
                state.NextRetryAtUtc = null;
                state.Reason = $"{failureReason ?? "Binance returned no data"} in three attempts at least 24 hours apart.";
            }
            else
            {
                state.Status = KlineGapStatuses.Pending;
                state.NextRetryAtUtc = now.AddHours(24);
                state.Reason = $"{failureReason ?? "Binance returned no data"}; retry deferred for 24 hours.";
            }
        }
        else if (failureReason is not null)
        {
            state.Status = KlineGapStatuses.Pending;
            state.NextRetryAtUtc = now.AddHours(24);
            state.Reason = $"{failureReason}; retry deferred for 24 hours.";
        }
        else
        {
            state.Status = KlineGapStatuses.Pending;
            state.AttemptCount = 0;
            state.NextRetryAtUtc = null;
            state.Reason = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        _cache?.Invalidate(state.Symbol);
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
            _cache?.Invalidate(symbol);
            return toAdd.Count;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
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

    internal static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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
