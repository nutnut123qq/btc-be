using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Backend.Options;
using System.Data.Common;

namespace Backend.Services;

/// <summary>
/// Kiểm tra độ đầy đủ và gaps của dữ liệu sau backfill/re-index.
/// </summary>
public class DataAuditService : IDataAuditService
{
    private readonly AppDbContext _db;
    private readonly ILogger<DataAuditService> _logger;
    private readonly DataAuditCache _cache;
    private readonly long? _backfillStartMs;
    private readonly IServiceScopeFactory? _scopeFactory;

    private static readonly string[] DefaultTimeframes =
    {
        "1m", "5m", "15m", "30m", "1h", "4h", "1d"
    };

    public DataAuditService(
        AppDbContext db,
        ILogger<DataAuditService> logger,
        DataAuditCache? cache = null,
        IOptions<KlinesIngestionOptions>? options = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _db = db;
        _logger = logger;
        _cache = cache ?? new DataAuditCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        _scopeFactory = scopeFactory;
        if (options is not null)
        {
            var start = options.Value.BackfillStartDate;
            if (start.Kind == DateTimeKind.Unspecified)
                start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
            _backfillStartMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        }
    }

    public void Invalidate(string symbol) => _cache.Invalidate(symbol);

    public async Task<DataAuditResponse> AuditAsync(
        string symbol,
        bool includeInventory = false,
        CancellationToken cancellationToken = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (_cache.TryGet(symbol, includeInventory, out var cached) && cached is not null)
            return cached;

        if (_scopeFactory is not null && _db.Database.IsNpgsql())
        {
            var postgresResponse = await AuditPostgresAsync(symbol, includeInventory, cancellationToken);
            _cache.Set(symbol, includeInventory, postgresResponse);
            return postgresResponse;
        }

        var audits = new List<TimeframeAudit>();
        foreach (var tf in DefaultTimeframes)
            audits.Add(await AuditTimeframeAsync(_db, symbol, tf, includeInventory, cancellationToken));
        var timeframeAudits = audits.ToArray();
        var news = await AuditNewsAsync(_db, cancellationToken);
        var rulesAlerts = await AuditRulesAlertsAsync(_db, symbol, cancellationToken);

        var response = new DataAuditResponse(
            symbol,
            DateTime.UtcNow,
            timeframeAudits,
            news,
            rulesAlerts);
        _cache.Set(symbol, includeInventory, response);
        return response;
    }

    private async Task<DataAuditResponse> AuditPostgresAsync(
        string symbol,
        bool includeInventory,
        CancellationToken cancellationToken)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var startMs = _backfillStartMs ?? new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var klineRowsTask = InReadScopeAsync(db => QueryPostgresAggregatesAsync(
            db, PostgresKlineAggregateSql, symbol, startMs, nowMs, cancellationToken));
        var auxiliaryTask = InReadScopeAsync(db => AuditPostgresAuxiliaryAsync(
            db, symbol, startMs, nowMs, includeInventory, cancellationToken));
        await Task.WhenAll(klineRowsTask, auxiliaryTask);
        var auxiliary = await auxiliaryTask;
        var rows = (await klineRowsTask).Concat(auxiliary.Rows).ToArray();
        Dictionary<string, long> Counts(string metric) => rows.Where(x => x.Metric == metric)
            .ToDictionary(x => x.Timeframe, x => x.Count);
        var patternCounts = Counts("CandlePatterns");
        var indicatorCounts = Counts("TechnicalIndicators");
        var vectorCounts = Counts("WindowVectors");
        var featureCounts = Counts("MlFeatureStores");
        var targetCounts = Counts("PriceTargets");
        var datasetCounts = Counts("WindowClassificationDatasets");
        var klinesByTimeframe = rows.Where(x => x.Metric == "Klines").ToDictionary(x => x.Timeframe);
        var ledgerInitialized = klinesByTimeframe.Count == DefaultTimeframes.Length
            && klinesByTimeframe.Values.All(x => x.Count == 1);
        var snapshots = DefaultTimeframes.Select(timeframe =>
        {
            klinesByTimeframe.TryGetValue(timeframe, out var row);
            var intervalMs = Timeframes.IntervalToMs(timeframe);
            return new PostgresTimeframeSnapshot(timeframe, nowMs - nowMs % intervalMs,
                0, row?.MinOpenTimeMs, row?.MaxOpenTimeMs);
        }).ToDictionary(x => x.Timeframe);
        var gapStates = auxiliary.GapStates.ToList();
        var liveGapRangeCounts = new Dictionary<string, long>();
        var ledgerStatuses = new Dictionary<string, string>();
        foreach (var timeframe in DefaultTimeframes)
        {
            var stats = snapshots[timeframe];
            var intervalMs = Timeframes.IntervalToMs(timeframe);
            var expectedBars = CalculateExpectedRange(0, startMs, stats.AuditEndOpenTimeMs, intervalMs).ExpectedBars;
            var timeframeStates = gapStates.Where(x => x.Timeframe == timeframe).ToArray();
            var overlapsLatest = stats.MaxOpenTimeMs.HasValue && timeframeStates.Any(x =>
                x.StartOpenTimeMs <= stats.MaxOpenTimeMs.Value && x.EndOpenTimeMs >= stats.MaxOpenTimeMs.Value);
            ExtendTrailingGapInMemory(symbol, timeframe, stats, intervalMs, timeframeStates, gapStates);
            timeframeStates = gapStates.Where(x => x.Timeframe == timeframe).ToArray();
            if (ShouldUseLiveFallback(ledgerInitialized, stats.MinOpenTimeMs, stats.MaxOpenTimeMs, overlapsLatest))
            {
                var exactTotal = await InReadScopeAsync(db => db.Klines.AsNoTracking().LongCountAsync(x =>
                    x.Symbol == symbol && x.Timeframe == timeframe
                    && x.OpenTimeMs >= startMs && x.OpenTimeMs <= stats.AuditEndOpenTimeMs, cancellationToken));
                stats = stats with { TotalKlines = exactTotal };
                snapshots[timeframe] = stats;
                var live = await InReadScopeAsync(db => DiscoverTransientGapsAsync(
                    db, symbol, timeframe, stats, startMs, cancellationToken));
                gapStates.RemoveAll(x => x.Timeframe == timeframe);
                gapStates.AddRange(live.States);
                liveGapRangeCounts[timeframe] = live.RangeCount;
                ledgerStatuses[timeframe] = GapLedgerStatuses.LiveFallback;
            }
            else
            {
                var missingBars = timeframeStates.Sum(x => x.MissingBars);
                stats = stats with { TotalKlines = Math.Max(0, expectedBars - missingBars) };
                snapshots[timeframe] = stats;
                ledgerStatuses[timeframe] = GapLedgerStatuses.Reconciled;
            }
        }
        var timeframeAudits = DefaultTimeframes.Select(timeframe =>
        {
            var stats = snapshots[timeframe];
            var intervalMs = Timeframes.IntervalToMs(timeframe);
            var expectedRange = CalculateExpectedRange(stats.TotalKlines, startMs, stats.AuditEndOpenTimeMs, intervalMs);
            long? expectedBars = expectedRange.ExpectedBars;
            var missingBars = expectedRange.MissingBars;
            var stateRows = gapStates.Where(x => x.Timeframe == timeframe).ToArray();
            var gaps = stateRows.OrderByDescending(x => x.MissingBars).ThenBy(x => x.StartOpenTimeMs).Take(10)
                .Select(state => new CandleGap(state.Id == 0 ? null : state.Id, state.StartOpenTimeMs, state.EndOpenTimeMs,
                    state.MissingBars, state.Status, state.AttemptCount, state.NextRetryAtUtc, state.Reason))
                .ToArray();
            var coverage = expectedBars > 0 ? Math.Min(100, (double)stats.TotalKlines / expectedBars.Value * 100) : 0;
            var latestAge = stats.MaxOpenTimeMs.HasValue
                ? Math.Max(0, (long)(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(stats.MaxOpenTimeMs.Value)).TotalSeconds)
                : (long?)null;

            return new TimeframeAudit(timeframe, stats.TotalKlines, stats.MinOpenTimeMs, stats.MaxOpenTimeMs,
                expectedBars, missingBars, liveGapRangeCounts.GetValueOrDefault(timeframe, stateRows.LongLength), coverage,
                gaps.Length == 0 ? 0 : gaps.Max(x => x.EndOpenTimeMs - x.StartOpenTimeMs + intervalMs),
                stateRows.LongCount(x => x.Status == KlineGapStatuses.Pending),
                stateRows.LongCount(x => x.Status == KlineGapStatuses.Unavailable), latestAge,
                ledgerStatuses[timeframe],
                includeInventory ? patternCounts.GetValueOrDefault(timeframe) : null,
                includeInventory ? indicatorCounts.GetValueOrDefault(timeframe) : null,
                includeInventory ? vectorCounts.GetValueOrDefault(timeframe) : null,
                includeInventory ? featureCounts.GetValueOrDefault(timeframe) : null,
                includeInventory ? targetCounts.GetValueOrDefault(timeframe) : null,
                includeInventory ? datasetCounts.GetValueOrDefault(timeframe) : null, gaps);
        }).ToArray();

        return new DataAuditResponse(symbol, DateTime.UtcNow, timeframeAudits, auxiliary.News, auxiliary.Rules);
    }

    private static async Task<TransientGapSnapshot> DiscoverTransientGapsAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        PostgresTimeframeSnapshot stats,
        long startMs,
        CancellationToken cancellationToken)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        var rows = (await KlineGapQuery.GetTopInternalGapsAsync(db, symbol, timeframe, intervalMs, 10,
            startMs, stats.AuditEndOpenTimeMs, cancellationToken)).ToList();
        var rangeCount = rows.FirstOrDefault()?.GapRangeCount ?? 0;
        if (stats.TotalKlines == 0 && stats.AuditEndOpenTimeMs >= startMs)
        {
            rows.Add(new KlineGapQuery.GapRow
            {
                StartOpenTimeMs = startMs,
                EndOpenTimeMs = stats.AuditEndOpenTimeMs,
                MissingBars = ((stats.AuditEndOpenTimeMs - startMs) / intervalMs) + 1
            });
            rangeCount = 1;
        }
        if (stats.MinOpenTimeMs.HasValue && stats.MinOpenTimeMs.Value - startMs >= intervalMs)
        {
            rows.Add(new KlineGapQuery.GapRow
            {
                StartOpenTimeMs = startMs,
                EndOpenTimeMs = stats.MinOpenTimeMs.Value - intervalMs,
                MissingBars = (stats.MinOpenTimeMs.Value - startMs) / intervalMs
            });
            rangeCount++;
        }
        if (stats.MaxOpenTimeMs.HasValue && stats.AuditEndOpenTimeMs - stats.MaxOpenTimeMs.Value >= intervalMs)
        {
            rows.Add(new KlineGapQuery.GapRow
            {
                StartOpenTimeMs = stats.MaxOpenTimeMs.Value + intervalMs,
                EndOpenTimeMs = stats.AuditEndOpenTimeMs,
                MissingBars = (stats.AuditEndOpenTimeMs - stats.MaxOpenTimeMs.Value) / intervalMs
            });
            rangeCount++;
        }
        var states = rows.OrderByDescending(x => x.MissingBars).ThenBy(x => x.StartOpenTimeMs).Take(10)
            .Select(x => new KlineGapState
            {
                Symbol = symbol,
                Timeframe = timeframe,
                StartOpenTimeMs = x.StartOpenTimeMs,
                EndOpenTimeMs = x.EndOpenTimeMs,
                MissingBars = x.MissingBars,
                Status = KlineGapStatuses.Pending,
                Reason = "DETECTED_NOT_PERSISTED"
            }).ToArray();
        return new TransientGapSnapshot(states, rangeCount);
    }

    internal static (long ExpectedBars, long MissingBars) CalculateExpectedRange(
        long totalKlines,
        long startMs,
        long endMs,
        long intervalMs)
    {
        if (intervalMs <= 0 || endMs < startMs)
            return (0, 0);
        var expectedBars = ((endMs - startMs) / intervalMs) + 1;
        return (expectedBars, Math.Max(0, expectedBars - totalKlines));
    }

    internal static bool ShouldUseLiveFallback(
        bool ledgerInitialized,
        long? minOpenTimeMs,
        long? maxOpenTimeMs,
        bool overlapsLatest) =>
        !ledgerInitialized || !minOpenTimeMs.HasValue || !maxOpenTimeMs.HasValue || overlapsLatest;

    private static void ExtendTrailingGapInMemory(
        string symbol,
        string timeframe,
        PostgresTimeframeSnapshot stats,
        long intervalMs,
        IReadOnlyList<KlineGapState> timeframeStates,
        List<KlineGapState> allStates)
    {
        if (!stats.MaxOpenTimeMs.HasValue || stats.AuditEndOpenTimeMs - stats.MaxOpenTimeMs.Value < intervalMs)
            return;
        var tailStart = stats.MaxOpenTimeMs.Value + intervalMs;
        var missingTailBars = (stats.AuditEndOpenTimeMs - stats.MaxOpenTimeMs.Value) / intervalMs;
        var tail = timeframeStates
            .Where(x => x.StartOpenTimeMs <= tailStart && x.EndOpenTimeMs >= tailStart)
            .OrderByDescending(x => x.EndOpenTimeMs)
            .FirstOrDefault();
        if (tail is not null)
        {
            if (CanExtendTrailingGap(tail))
            {
                tail.StartOpenTimeMs = tailStart;
                tail.EndOpenTimeMs = stats.AuditEndOpenTimeMs;
                tail.MissingBars = missingTailBars;
            }
            else if (tail.StartOpenTimeMs == tailStart)
            {
                var extension = CalculateTrailingExtension(tail.EndOpenTimeMs, stats.AuditEndOpenTimeMs, intervalMs);
                if (extension.HasValue)
                {
                    allStates.Add(new KlineGapState
                    {
                        Symbol = symbol,
                        Timeframe = timeframe,
                        StartOpenTimeMs = extension.Value.StartOpenTimeMs,
                        EndOpenTimeMs = stats.AuditEndOpenTimeMs,
                        MissingBars = extension.Value.MissingBars,
                        Status = KlineGapStatuses.Pending,
                        Reason = "TRAILING_GAP_NOT_PERSISTED"
                    });
                }
            }
            return;
        }
        allStates.Add(new KlineGapState
        {
            Symbol = symbol,
            Timeframe = timeframe,
            StartOpenTimeMs = tailStart,
            EndOpenTimeMs = stats.AuditEndOpenTimeMs,
            MissingBars = missingTailBars,
            Status = KlineGapStatuses.Pending,
            Reason = "TRAILING_GAP_NOT_PERSISTED"
        });
    }

    internal static bool CanExtendTrailingGap(KlineGapState state) =>
        state.Status == KlineGapStatuses.Pending
        && state.AttemptCount == 0
        && state.NextRetryAtUtc is null
        && (state.Reason == "BOOTSTRAP_DISCOVERY" || state.Reason == "TRAILING_GAP_NOT_PERSISTED");

    internal static (long StartOpenTimeMs, long MissingBars)? CalculateTrailingExtension(
        long persistedEndOpenTimeMs,
        long auditEndOpenTimeMs,
        long intervalMs)
    {
        if (intervalMs <= 0 || auditEndOpenTimeMs - persistedEndOpenTimeMs < intervalMs)
            return null;
        return (persistedEndOpenTimeMs + intervalMs,
            (auditEndOpenTimeMs - persistedEndOpenTimeMs) / intervalMs);
    }

    private static async Task<PostgresAuxiliarySnapshot> AuditPostgresAuxiliaryAsync(
        AppDbContext db,
        string symbol,
        long startMs,
        long nowMs,
        bool includeInventory,
        CancellationToken cancellationToken)
    {
        var rows = includeInventory
            ? await QueryPostgresAggregatesAsync(db, PostgresDerivedAggregateSql, symbol, startMs, nowMs, cancellationToken)
            : [];
        var gapStates = await db.KlineGapStates.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Status != KlineGapStatuses.Filled)
            .ToListAsync(cancellationToken);
        var news = await AuditNewsAsync(db, cancellationToken);
        var rules = await AuditRulesAlertsAsync(db, symbol, cancellationToken);
        return new PostgresAuxiliarySnapshot(rows, gapStates, news, rules);
    }

    private static async Task<List<PostgresAggregateRow>> QueryPostgresAggregatesAsync(
        AppDbContext db,
        string sql,
        string symbol,
        long startMs,
        long nowMs,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "symbol", symbol);
        AddParameter(command, "startMs", startMs);
        AddParameter(command, "nowMs", nowMs);
        var rows = new List<PostgresAggregateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PostgresAggregateRow
            {
                Metric = reader.GetString(0),
                Timeframe = reader.GetString(1),
                Count = reader.GetInt64(2),
                MinOpenTimeMs = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                MaxOpenTimeMs = reader.IsDBNull(4) ? null : reader.GetInt64(4)
            });
        }
        return rows;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task<T> InReadScopeAsync<T>(Func<AppDbContext, Task<T>> operation)
    {
        using var scope = _scopeFactory!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return await operation(db);
    }

    private sealed class PostgresAggregateRow
    {
        public string Metric { get; set; } = "";
        public string Timeframe { get; set; } = "";
        public long Count { get; set; }
        public long? MinOpenTimeMs { get; set; }
        public long? MaxOpenTimeMs { get; set; }
    }

    private const string PostgresKlineAggregateSql = """
        WITH config("Timeframe", interval_ms) AS (
            VALUES ('1m',60000::bigint),('5m',300000::bigint),('15m',900000::bigint),
                   ('30m',1800000::bigint),('1h',3600000::bigint),('4h',14400000::bigint),('1d',86400000::bigint)
        )
        SELECT 'Klines' AS "Metric", c."Timeframe",
               (SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM "__EFMigrationsHistory"
                            WHERE "MigrationId" = '20260831075707_BootstrapKlineGapStates')
                    AND EXISTS (SELECT 1 FROM "KlineGapStates" WHERE "Symbol" = @symbol)
                    THEN 1::bigint ELSE 0::bigint END) AS "Count",
               (SELECT k."OpenTimeMs" FROM "Klines" k
                WHERE k."Symbol"=@symbol AND k."Timeframe"=c."Timeframe" AND k."OpenTimeMs">=@startMs
                ORDER BY k."OpenTimeMs" LIMIT 1) AS "MinOpenTimeMs",
               (SELECT k."OpenTimeMs" FROM "Klines" k
                WHERE k."Symbol"=@symbol AND k."Timeframe"=c."Timeframe" AND k."OpenTimeMs"<=@nowMs
                ORDER BY k."OpenTimeMs" DESC LIMIT 1) AS "MaxOpenTimeMs"
        FROM config c
        """;
    private const string PostgresDerivedAggregateSql = """
        SELECT 'CandlePatterns' AS "Metric", "Timeframe", count(*)::bigint AS "Count", NULL::bigint AS "MinOpenTimeMs", NULL::bigint AS "MaxOpenTimeMs"
        FROM "CandlePatterns" WHERE "Symbol" = @symbol GROUP BY "Timeframe"
        UNION ALL SELECT 'TechnicalIndicators', "Timeframe", count(*)::bigint, NULL::bigint, NULL::bigint
        FROM "TechnicalIndicators" WHERE "Symbol" = @symbol GROUP BY "Timeframe"
        UNION ALL SELECT 'WindowVectors', "Timeframe", count(*)::bigint, NULL::bigint, NULL::bigint
        FROM "WindowVectors" WHERE "Symbol" = @symbol GROUP BY "Timeframe"
        UNION ALL SELECT 'MlFeatureStores', "Timeframe", count(*)::bigint, NULL::bigint, NULL::bigint
        FROM "MlFeatureStores" WHERE "Symbol" = @symbol GROUP BY "Timeframe"
        UNION ALL SELECT 'PriceTargets', "Timeframe", count(*)::bigint, NULL::bigint, NULL::bigint
        FROM "PriceTargets" WHERE "Symbol" = @symbol GROUP BY "Timeframe"
        UNION ALL SELECT 'WindowClassificationDatasets', "Timeframe", count(*)::bigint, NULL::bigint, NULL::bigint
        FROM "WindowClassificationDatasets" WHERE "Symbol" = @symbol GROUP BY "Timeframe"
        """;
    private sealed record PostgresTimeframeSnapshot(
        string Timeframe,
        long AuditEndOpenTimeMs,
        long TotalKlines,
        long? MinOpenTimeMs,
        long? MaxOpenTimeMs);
    private sealed record PostgresAuxiliarySnapshot(
        IReadOnlyList<PostgresAggregateRow> Rows,
        IReadOnlyList<KlineGapState> GapStates,
        NewsAudit News,
        RulesAlertsAudit Rules);
    private sealed record TransientGapSnapshot(IReadOnlyList<KlineGapState> States, long RangeCount);

    private async Task<TimeframeAudit> AuditTimeframeAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        bool includeInventory,
        CancellationToken cancellationToken)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var auditEndMs = intervalMs > 0 ? nowMs - nowMs % intervalMs : nowMs;
        var klineQuery = db.Klines.Where(k => k.Symbol == symbol && k.Timeframe == timeframe);
        if (_backfillStartMs.HasValue)
            klineQuery = klineQuery.Where(k => k.OpenTimeMs >= _backfillStartMs.Value && k.OpenTimeMs <= auditEndMs);
        var klineStats = await klineQuery
            .GroupBy(k => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Min = g.Min(k => k.OpenTimeMs),
                Max = g.Max(k => k.OpenTimeMs)
            })
            .FirstOrDefaultAsync(cancellationToken);

        long klineCount = klineStats?.Count ?? 0;
        long? minOpenTime = klineStats?.Min;
        long? maxOpenTime = klineStats?.Max;

        long? expectedBars = null;
        long missingBars = 0;

        if (klineCount > 0 && minOpenTime.HasValue && maxOpenTime.HasValue && intervalMs > 0)
        {
            var expectedStart = _backfillStartMs ?? minOpenTime.Value;
            var expectedEnd = _backfillStartMs.HasValue ? auditEndMs : maxOpenTime.Value;
            expectedBars = ((expectedEnd - expectedStart) / intervalMs) + 1;
            missingBars = Math.Max(0, expectedBars.Value - klineCount);
        }

        // Chạy tuần tự trên cùng một DbContext để tránh concurrency.
        long? candlePatternsCount = includeInventory ? await db.CandlePatterns.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken) : null;
        long? technicalIndicatorsCount = includeInventory ? await db.TechnicalIndicators.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken) : null;
        long? windowVectorsCount = includeInventory ? await db.WindowVectors.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken) : null;
        long? mlFeatureStoresCount = includeInventory ? await db.MlFeatureStores.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken) : null;
        long? priceTargetsCount = includeInventory ? await db.PriceTargets.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken) : null;
        long? windowClassificationDatasetsCount = includeInventory ? await db.WindowClassificationDatasets.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken) : null;

        var gapCounts = await db.KlineGapStates.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && (x.Status == KlineGapStatuses.Pending || x.Status == KlineGapStatuses.Unavailable))
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);
        var pendingGapCount = gapCounts.FirstOrDefault(x => x.Status == KlineGapStatuses.Pending)?.Count ?? 0;
        var unavailableGapCount = gapCounts.FirstOrDefault(x => x.Status == KlineGapStatuses.Unavailable)?.Count ?? 0;

        var internalGapRows = await KlineGapQuery.GetTopInternalGapsAsync(
            db, symbol, timeframe, intervalMs, 10,
            _backfillStartMs, _backfillStartMs.HasValue ? auditEndMs : null, cancellationToken);
        var gapRangeCount = internalGapRows.FirstOrDefault()?.GapRangeCount ?? 0;
        var gapRows = internalGapRows.ToList();
        if (_backfillStartMs.HasValue && minOpenTime.HasValue && maxOpenTime.HasValue)
        {
            if (minOpenTime.Value - _backfillStartMs.Value >= intervalMs)
            {
                gapRows.Add(new KlineGapQuery.GapRow
                {
                    StartOpenTimeMs = _backfillStartMs.Value,
                    EndOpenTimeMs = minOpenTime.Value - intervalMs,
                    MissingBars = (minOpenTime.Value - _backfillStartMs.Value) / intervalMs
                });
                gapRangeCount++;
            }
            if (auditEndMs - maxOpenTime.Value >= intervalMs)
            {
                gapRows.Add(new KlineGapQuery.GapRow
                {
                    StartOpenTimeMs = maxOpenTime.Value + intervalMs,
                    EndOpenTimeMs = auditEndMs,
                    MissingBars = (auditEndMs - maxOpenTime.Value) / intervalMs
                });
                gapRangeCount++;
            }
        }
        gapRows = gapRows.OrderByDescending(x => x.MissingBars).ThenBy(x => x.StartOpenTimeMs).Take(10).ToList();
        var minGapStart = gapRows.Count == 0 ? 0 : gapRows.Min(g => g.StartOpenTimeMs);
        var maxGapEnd = gapRows.Count == 0 ? 0 : gapRows.Max(g => g.EndOpenTimeMs);
        var states = gapRows.Count == 0
            ? []
            : await db.KlineGapStates.AsNoTracking()
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && x.Status != KlineGapStatuses.Filled
                    && x.StartOpenTimeMs <= maxGapEnd
                    && x.EndOpenTimeMs >= minGapStart)
                .ToListAsync(cancellationToken);
        var gaps = gapRows.Select(g =>
        {
            var state = states.Where(x => x.StartOpenTimeMs <= g.StartOpenTimeMs && x.EndOpenTimeMs >= g.EndOpenTimeMs)
                .OrderBy(x => x.EndOpenTimeMs - x.StartOpenTimeMs)
                .FirstOrDefault();
            return new CandleGap(state?.Id, g.StartOpenTimeMs, g.EndOpenTimeMs, g.MissingBars,
                state?.Status, state?.AttemptCount ?? 0, state?.NextRetryAtUtc, state?.Reason);
        }).ToArray();

        double dataCoveragePct = 0;
        if (expectedBars.HasValue && expectedBars.Value > 0)
        {
            dataCoveragePct = (double)klineCount / expectedBars.Value * 100.0;
            if (dataCoveragePct > 100) dataCoveragePct = 100;
        }

        long largestGapMs = gaps.Length > 0 ? gaps.Max(g => g.EndOpenTimeMs - g.StartOpenTimeMs + intervalMs) : 0;
        long? latestCandleAgeSeconds = maxOpenTime.HasValue
            ? Math.Max(0, (long)(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(maxOpenTime.Value)).TotalSeconds)
            : null;

        return new TimeframeAudit(
            timeframe,
            klineCount,
            minOpenTime,
            maxOpenTime,
            expectedBars,
            missingBars,
            gapRangeCount,
            dataCoveragePct,
            largestGapMs,
            pendingGapCount,
            unavailableGapCount,
            latestCandleAgeSeconds,
            GapLedgerStatuses.LiveFallback,
            candlePatternsCount,
            technicalIndicatorsCount,
            windowVectorsCount,
            mlFeatureStoresCount,
            priceTargetsCount,
            windowClassificationDatasetsCount,
            gaps);
    }

    private static async Task<NewsAudit> AuditNewsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var articleStats = await db.NewsArticles
            .GroupBy(a => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Min = g.Min(a => a.PublishedAt),
                Max = g.Max(a => a.PublishedAt)
            })
            .FirstOrDefaultAsync(cancellationToken);

        long chunks = await db.NewsChunks.LongCountAsync(cancellationToken);

        return new NewsAudit(
            articleStats?.Count ?? 0,
            chunks,
            articleStats?.Min,
            articleStats?.Max);
    }

    private static async Task<RulesAlertsAudit> AuditRulesAlertsAsync(AppDbContext db, string symbol, CancellationToken cancellationToken)
    {
        var rules = await db.CandleSequenceRules.LongCountAsync(r => r.Symbol == symbol, cancellationToken);
        var signals = await db.CandleSequenceSignals.LongCountAsync(s => s.Symbol == symbol, cancellationToken);
        var alerts = await db.AppAlerts.LongCountAsync(cancellationToken);

        return new RulesAlertsAudit(rules, signals, alerts);
    }
}
