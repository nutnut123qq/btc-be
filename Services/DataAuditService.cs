using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Kiểm tra độ đầy đủ và gaps của dữ liệu sau backfill/re-index.
/// </summary>
public class DataAuditService : IDataAuditService
{
    private readonly AppDbContext _db;
    private readonly ILogger<DataAuditService> _logger;

    private static readonly string[] DefaultTimeframes =
    {
        "1m", "5m", "15m", "30m", "1h", "4h", "1d"
    };

    public DataAuditService(AppDbContext db, ILogger<DataAuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DataAuditResponse> AuditAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var timeframeAudits = new Dictionary<string, TimeframeAudit>();

        foreach (var tf in DefaultTimeframes)
        {
            timeframeAudits[tf] = await AuditTimeframeAsync(symbol, tf, cancellationToken);
        }

        var news = await AuditNewsAsync(cancellationToken);
        var rulesAlerts = await AuditRulesAlertsAsync(symbol, cancellationToken);

        return new DataAuditResponse(
            symbol,
            DateTime.UtcNow,
            timeframeAudits,
            news,
            rulesAlerts);
    }

    private async Task<TimeframeAudit> AuditTimeframeAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);

        var klineStats = await _db.Klines
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
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

        long? expectedCount = null;
        long gapCount = 0;

        if (klineCount > 0 && minOpenTime.HasValue && maxOpenTime.HasValue && intervalMs > 0)
        {
            expectedCount = ((maxOpenTime.Value - minOpenTime.Value) / intervalMs) + 1;
            gapCount = Math.Max(0, expectedCount.Value - klineCount);
        }

        // Chạy tuần tự trên cùng một DbContext để tránh concurrency.
        var candlePatternsCount = await _db.CandlePatterns.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken);
        var technicalIndicatorsCount = await _db.TechnicalIndicators.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken);
        var windowVectorsCount = await _db.WindowVectors.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken);
        var mlFeatureStoresCount = await _db.MlFeatureStores.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken);
        var priceTargetsCount = await _db.PriceTargets.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken);
        var windowClassificationDatasetsCount = await _db.WindowClassificationDatasets.LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe, cancellationToken);

        var gaps = await GetTopGapsAsync(symbol, timeframe, intervalMs, 10, cancellationToken);

        return new TimeframeAudit(
            klineCount,
            minOpenTime,
            maxOpenTime,
            expectedCount,
            gapCount,
            candlePatternsCount,
            technicalIndicatorsCount,
            windowVectorsCount,
            mlFeatureStoresCount,
            priceTargetsCount,
            windowClassificationDatasetsCount,
            gaps);
    }

    private async Task<IReadOnlyList<CandleGap>> GetTopGapsAsync(
        string symbol,
        string timeframe,
        long intervalMs,
        int limit,
        CancellationToken cancellationToken)
    {
        if (intervalMs <= 0)
            return Array.Empty<CandleGap>();

        var openTimes = await _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderBy(k => k.OpenTimeMs)
            .Select(k => k.OpenTimeMs)
            .ToListAsync(cancellationToken);

        var gaps = new List<CandleGap>();
        for (int i = 1; i < openTimes.Count; i++)
        {
            var diff = openTimes[i] - openTimes[i - 1];
            if (diff > intervalMs)
            {
                var missingCount = (diff / intervalMs) - 1;
                gaps.Add(new CandleGap(
                    openTimes[i - 1] + intervalMs,
                    openTimes[i] - intervalMs,
                    missingCount));
            }
        }

        return gaps
            .OrderByDescending(g => g.MissingCount)
            .Take(limit)
            .ToList();
    }

    private async Task<NewsAudit> AuditNewsAsync(CancellationToken cancellationToken)
    {
        var articleStats = await _db.NewsArticles
            .GroupBy(a => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Min = g.Min(a => a.PublishedAt),
                Max = g.Max(a => a.PublishedAt)
            })
            .FirstOrDefaultAsync(cancellationToken);

        long chunks = await _db.NewsChunks.LongCountAsync(cancellationToken);

        return new NewsAudit(
            articleStats?.Count ?? 0,
            chunks,
            articleStats?.Min,
            articleStats?.Max);
    }

    private async Task<RulesAlertsAudit> AuditRulesAlertsAsync(string symbol, CancellationToken cancellationToken)
    {
        var rules = await _db.CandleSequenceRules.LongCountAsync(r => r.Symbol == symbol, cancellationToken);
        var signals = await _db.CandleSequenceSignals.LongCountAsync(s => s.Symbol == symbol, cancellationToken);
        var alerts = await _db.AppAlerts.LongCountAsync(cancellationToken);

        return new RulesAlertsAudit(rules, signals, alerts);
    }
}
