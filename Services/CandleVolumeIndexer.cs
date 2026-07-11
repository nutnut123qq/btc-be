using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Pre-compute và lưu các chỉ số volume phân tích (SMA20, anomaly ratio, vs previous, vs max10).
/// </summary>
public class CandleVolumeIndexer
{
    private readonly AppDbContext _db;
    private readonly ILogger<CandleVolumeIndexer> _logger;
    private readonly IndexingOptions _options;

    public CandleVolumeIndexer(
        AppDbContext db,
        ILogger<CandleVolumeIndexer> logger,
        IOptions<IndexingOptions> options)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken = default)
    {
        if (klines.Count == 0) return 0;

        var startMs = klines[0].OpenTimeMs;
        var endMs = klines[^1].OpenTimeMs;
        var existingTimes = await LoadExistingTimesAsync(symbol, timeframe, startMs, endMs, cancellationToken);

        return await IndexAsync(symbol, timeframe, klines, existingTimes, cancellationToken);
    }

    /// <summary>
    /// Index với existing times đã có sẵn. Dùng khi caller muốn cache keys giữa các chunk.
    /// </summary>
    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        HashSet<long> existingTimes,
        CancellationToken cancellationToken = default)
    {
        if (klines.Count == 0) return 0;

        var batchSize = Math.Max(100, _options.VolumeStatsBatchSize);
        var batch = new List<CandleVolumeStats>(batchSize);
        var totalAdded = 0;

        var (sma20, vsMax10) = ComputeRollingStats(klines);

        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];
            if (existingTimes.Contains(k.OpenTimeMs)) continue;

            var ratio = sma20[i] > 0 ? (double)(k.Volume / sma20[i]) : 1.0;
            var vsPrev = i > 0 && klines[i - 1].Volume > 0
                ? (double)(k.Volume / klines[i - 1].Volume)
                : 1.0;
            var trend = DetermineTrend(klines, i);

            batch.Add(new CandleVolumeStats
            {
                Symbol = symbol,
                Timeframe = timeframe,
                OpenTimeMs = k.OpenTimeMs,
                Volume = k.Volume,
                VolumeSma20 = sma20[i],
                VolumeAnomalyRatio = ratio,
                VolumeVsPrevious = vsPrev,
                VolumeVsMax10 = vsMax10[i],
                VolumeTrend = trend
            });

            if (batch.Count >= batchSize)
            {
                _db.CandleVolumeStats.AddRange(batch);
                await _db.SaveChangesAsync(cancellationToken);
                totalAdded += batch.Count;
                batch.Clear();
                _db.ChangeTracker.Clear();
            }
        }

        if (batch.Count > 0)
        {
            _db.CandleVolumeStats.AddRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            totalAdded += batch.Count;
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation("Volume stats indexed {Count} bars for {Symbol} {Timeframe}", totalAdded, symbol, timeframe);
        return totalAdded;
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

    private async Task<HashSet<long>> LoadExistingTimesAsync(
        string symbol,
        string timeframe,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        var times = await _db.CandleVolumeStats
            .AsNoTracking()
            .Where(x =>
                x.Symbol == symbol &&
                x.Timeframe == timeframe &&
                x.OpenTimeMs >= startMs &&
                x.OpenTimeMs <= endMs)
            .Select(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);

        return new HashSet<long>(times);
    }

    /// <summary>
    /// Tính SMA20 và VsMax10 bằng sliding window O(1) mỗi nến.
    /// </summary>
    private static (decimal[] Sma20, double[] VsMax10) ComputeRollingStats(IReadOnlyList<KlineDto> klines)
    {
        var n = klines.Count;
        var sma20 = new decimal[n];
        var vsMax10 = new double[n];

        var sum20 = 0m;
        var volQueue20 = new Queue<decimal>();
        var maxDeque = new LinkedList<(int Index, decimal Volume)>(); // descending max deque for period 10

        for (int i = 0; i < n; i++)
        {
            // Thêm nến trước đó vào các cửa sổ trước khi tính cho nến hiện tại
            if (i > 0)
            {
                var prevVol = klines[i - 1].Volume;

                sum20 += prevVol;
                volQueue20.Enqueue(prevVol);
                if (volQueue20.Count > 20)
                    sum20 -= volQueue20.Dequeue();

                while (maxDeque.Last is { } lastNode && lastNode.Value.Volume <= prevVol)
                    maxDeque.RemoveLast();
                maxDeque.AddLast((i - 1, prevVol));
                if (maxDeque.First is { } firstNode && firstNode.Value.Index <= i - 11)
                    maxDeque.RemoveFirst();
            }

            sma20[i] = volQueue20.Count > 0 ? sum20 / volQueue20.Count : 0;

            var currentMax = maxDeque.First is { } currentMaxNode ? currentMaxNode.Value.Volume : 0m;
            vsMax10[i] = currentMax > 0 ? (double)(klines[i].Volume / currentMax) : 1.0;
        }

        return (sma20, vsMax10);
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
