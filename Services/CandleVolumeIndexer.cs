using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Pre-compute và lưu các chỉ số volume phân tích (SMA20, anomaly ratio, vs previous, vs max10).
/// </summary>
public class CandleVolumeIndexer
{
    private readonly AppDbContext _db;
    private readonly ILogger<CandleVolumeIndexer> _logger;

    public CandleVolumeIndexer(AppDbContext db, ILogger<CandleVolumeIndexer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken = default)
    {
        if (klines.Count == 0) return 0;

        var existingTimesList = await _db.CandleVolumeStats
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .Select(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);
        var existingTimes = new HashSet<long>(existingTimesList);

        var toAdd = new List<CandleVolumeStats>();

        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];
            if (existingTimes.Contains(k.OpenTimeMs)) continue;

            var sma20 = ComputeSma(klines, i, period: 20);
            var ratio = sma20 > 0 ? (double)(k.Volume / sma20) : 1.0;
            var vsPrev = i > 0 && klines[i - 1].Volume > 0
                ? (double)(k.Volume / klines[i - 1].Volume)
                : 1.0;
            var vsMax10 = ComputeVsMax(klines, i, period: 10);
            var trend = DetermineTrend(klines, i);

            toAdd.Add(new CandleVolumeStats
            {
                Symbol = symbol,
                Timeframe = timeframe,
                OpenTimeMs = k.OpenTimeMs,
                Volume = k.Volume,
                VolumeSma20 = sma20,
                VolumeAnomalyRatio = ratio,
                VolumeVsPrevious = vsPrev,
                VolumeVsMax10 = vsMax10,
                VolumeTrend = trend
            });
        }

        if (toAdd.Count > 0)
        {
            _db.CandleVolumeStats.AddRange(toAdd);
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("Volume stats indexed {Count} bars for {Symbol} {Timeframe}", toAdd.Count, symbol, timeframe);
        return toAdd.Count;
    }

    public async Task<IReadOnlyList<CandleVolumeStats>> GetStatsAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        return await _db.CandleVolumeStats
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .OrderBy(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);
    }

    private static decimal ComputeSma(IReadOnlyList<KlineDto> klines, int idx, int period)
    {
        int start = Math.Max(0, idx - period);
        int count = idx - start;
        if (count <= 0) return 0;
        decimal sum = 0;
        for (int i = start; i < idx; i++)
            sum += klines[i].Volume;
        return sum / count;
    }

    private static double ComputeVsMax(IReadOnlyList<KlineDto> klines, int idx, int period)
    {
        int start = Math.Max(0, idx - period);
        if (start >= idx) return 1.0;
        var max = 0m;
        for (int i = start; i < idx; i++)
            if (klines[i].Volume > max) max = klines[i].Volume;
        if (max <= 0) return 1.0;
        return (double)(klines[idx].Volume / max);
    }

    private static string DetermineTrend(IReadOnlyList<KlineDto> klines, int idx)
    {
        if (idx < 2) return "normal";
        var v0 = klines[idx - 2].Volume;
        var v1 = klines[idx - 1].Volume;
        var v2 = klines[idx].Volume;
        if (v2 > v1 && v1 > v0) return "increasing";
        if (v2 < v1 && v1 < v0) return "decreasing";
        return "normal";
    }
}
