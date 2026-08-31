using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

internal static class KlineGapQuery
{
    internal sealed class GapRow
    {
        public long StartOpenTimeMs { get; set; }
        public long EndOpenTimeMs { get; set; }
        public long MissingBars { get; set; }
        public long GapRangeCount { get; set; }
    }

    public static async Task<IReadOnlyList<GapRow>> GetTopInternalGapsAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        long intervalMs,
        int limit,
        long? startMs,
        long? endMs,
        CancellationToken cancellationToken)
    {
        if (intervalMs <= 0 || limit <= 0)
            return [];

        var query = db.Klines.AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe);
        if (startMs.HasValue)
            query = query.Where(k => k.OpenTimeMs >= startMs.Value);
        if (endMs.HasValue)
            query = query.Where(k => k.OpenTimeMs <= endMs.Value);

        if (!db.Database.IsRelational())
        {
            var times = await query.OrderBy(k => k.OpenTimeMs)
                .Select(k => k.OpenTimeMs)
                .ToListAsync(cancellationToken);
            var all = times.Zip(times.Skip(1))
                .Where(pair => pair.Second - pair.First > intervalMs)
                .Select(pair => new GapRow
                {
                    StartOpenTimeMs = pair.First + intervalMs,
                    EndOpenTimeMs = pair.Second - intervalMs,
                    MissingBars = ((pair.Second - pair.First) / intervalMs) - 1
                })
                .OrderByDescending(x => x.MissingBars)
                .ThenBy(x => x.StartOpenTimeMs)
                .ToArray();
            foreach (var gap in all)
                gap.GapRangeCount = all.LongLength;
            return all.Take(limit).ToArray();
        }

        var rangeStart = startMs ?? long.MinValue;
        var rangeEnd = endMs ?? long.MaxValue;
        return await db.Database.SqlQuery<GapRow>($"""
            WITH ordered AS (
                SELECT "OpenTimeMs",
                       LAG("OpenTimeMs") OVER (ORDER BY "OpenTimeMs") AS previous_open_time_ms
                FROM "Klines"
                WHERE "Symbol" = {symbol}
                  AND "Timeframe" = {timeframe}
                  AND "OpenTimeMs" >= {rangeStart}
                  AND "OpenTimeMs" <= {rangeEnd}
            )
            SELECT (previous_open_time_ms + {intervalMs})::bigint AS "StartOpenTimeMs",
                   ("OpenTimeMs" - {intervalMs})::bigint AS "EndOpenTimeMs",
                   (("OpenTimeMs" - previous_open_time_ms) / {intervalMs} - 1)::bigint AS "MissingBars",
                   COUNT(*) OVER()::bigint AS "GapRangeCount"
            FROM ordered
            WHERE previous_open_time_ms IS NOT NULL
              AND "OpenTimeMs" - previous_open_time_ms > {intervalMs}
            ORDER BY "MissingBars" DESC, "StartOpenTimeMs"
            LIMIT {limit}
            """).ToListAsync(cancellationToken);
    }
}
