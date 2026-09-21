using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Point-in-time access for derivative observations. Legacy rows without an
/// observed availability timestamp are intentionally ineligible rather than
/// being assigned an invented historical receipt time.
/// </summary>
public static class DerivativeAsOfQuery
{
    public static IQueryable<FuturesMetric> AvailableAsOf(
        this IQueryable<FuturesMetric> query,
        long decisionTimeMs)
    {
        var decisionTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(decisionTimeMs);
        return query.Where(x =>
            x.SourceEventTimeMs.HasValue &&
            x.SourceEventTimeMs.Value <= decisionTimeMs &&
            x.ReceivedAtUtc.HasValue &&
            x.ReceivedAtUtc.Value <= decisionTimeUtc &&
            x.AvailableTimeMs.HasValue &&
            x.AvailableTimeMs.Value <= decisionTimeMs);
    }

    public static IQueryable<MarketMetrics> AvailableAsOf(
        this IQueryable<MarketMetrics> query,
        long decisionTimeMs)
    {
        var decisionTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(decisionTimeMs);
        return query.Where(x =>
            x.SourceEventTimeMs.HasValue &&
            x.SourceEventTimeMs.Value <= decisionTimeMs &&
            x.ReceivedAtUtc.HasValue &&
            x.ReceivedAtUtc.Value <= decisionTimeUtc &&
            x.AvailableTimeMs.HasValue &&
            x.AvailableTimeMs.Value <= decisionTimeMs);
    }

    public static Task<FuturesMetric?> LatestFuturesAsync(
        AppDbContext db,
        string symbol,
        long decisionTimeMs,
        CancellationToken cancellationToken = default) => db.FuturesMetrics
            .AsNoTracking()
            .Where(x => x.Symbol == symbol)
            .AvailableAsOf(decisionTimeMs)
            .OrderByDescending(x => x.SourceEventTimeMs)
            .ThenByDescending(x => x.AvailableTimeMs)
            .FirstOrDefaultAsync(cancellationToken);

    public static Task<MarketMetrics?> LatestMarketAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        long decisionTimeMs,
        CancellationToken cancellationToken = default) => db.MarketMetrics
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .AvailableAsOf(decisionTimeMs)
            .OrderByDescending(x => x.SourceEventTimeMs)
            .ThenByDescending(x => x.AvailableTimeMs)
            .FirstOrDefaultAsync(cancellationToken);
}
