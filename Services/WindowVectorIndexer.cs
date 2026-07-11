using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public class WindowVectorIndexer : IWindowVectorIndexer
{
    public const int VectorVersion = 2;
    private const double Epsilon = 0.0000001;

    private readonly AppDbContext _db;
    private readonly IndexingOptions _options;

    public WindowVectorIndexer(AppDbContext db, IOptions<IndexingOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    /// <summary>
    /// Xây dựng window vectors cho nhiều feature types và window sizes trong MỘT vòng lặp duyệt klines.
    /// Chỉ query DB một lần để lấy existing keys trong range của klines.
    /// </summary>
    public async Task<int> BuildAllForTimeframeAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        IEnumerable<string> featureTypes,
        IEnumerable<int> windowSizes,
        CancellationToken cancellationToken = default)
    {
        var normalizedFeatureTypes = featureTypes
            .Select(PatternVectorFeatureType.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wsList = windowSizes.Distinct().OrderBy(x => x).ToList();
        if (wsList.Count == 0) wsList = new List<int> { 10, 15, 25 };

        var minWindowSize = wsList.Min();
        if (klines.Count < minWindowSize) return 0;

        var startMs = klines[0].OpenTimeMs;
        var endMs = klines[^1].OpenTimeMs;

        // Xóa vector cũ có version khác trong range để tránh stale data
        await DeleteStaleVectorsAsync(symbol, timeframe, normalizedFeatureTypes, wsList, startMs, endMs, cancellationToken);

        // Load existing keys một lần cho tất cả combinations
        var existingKeys = await LoadExistingKeysAsync(
            symbol, timeframe, normalizedFeatureTypes, wsList, startMs, endMs, cancellationToken);

        var batchSize = Math.Max(100, _options.WindowVectorBatchSize);
        var batch = new List<WindowVector>(batchSize);
        var upserted = 0;
        var now = DateTime.UtcNow;

        foreach (var ws in wsList)
        {
            if (klines.Count < ws) continue;

            for (var start = 0; start <= klines.Count - ws; start++)
            {
                var window = new SliceView<KlineDto>(klines, start, ws);
                var windowStartMs = window[0].OpenTimeMs;
                var windowEndMs = window[^1].OpenTimeMs;

                foreach (var ft in normalizedFeatureTypes)
                {
                    if (existingKeys[(ft, ws)].Contains(windowStartMs)) continue;

                    var vector = BuildVector(window, ft);
                    if (vector is null) continue;

                    var norm = (float)Math.Sqrt(vector.Sum(x => x * x));

                    batch.Add(new WindowVector
                    {
                        Symbol = symbol,
                        Timeframe = timeframe,
                        FeatureType = ft,
                        WindowSize = ws,
                        StartTimeMs = windowStartMs,
                        EndTimeMs = windowEndMs,
                        Vector = vector,
                        VectorDim = vector.Length,
                        VectorNorm = norm,
                        Version = VectorVersion,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }

                if (batch.Count >= batchSize)
                {
                    _db.WindowVectors.AddRange(batch);
                    await _db.SaveChangesAsync(cancellationToken);
                    upserted += batch.Count;
                    batch.Clear();
                    _db.ChangeTracker.Clear();
                }
            }
        }

        if (batch.Count > 0)
        {
            _db.WindowVectors.AddRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            upserted += batch.Count;
            _db.ChangeTracker.Clear();
        }

        return upserted;
    }

    /// <summary>
    /// Giữ lại để tương thích với caller cũ. Chỉ load existing keys trong range cần thiết.
    /// </summary>
    public async Task<int> BuildFullAsync(
        string symbol,
        string timeframe,
        string featureType,
        int lookbackBars,
        int windowSize,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderBy(k => k.OpenTimeMs)
            .Select(k => KlineMapper.ToDto(k))
            .ToListAsync(cancellationToken);

        var effectiveRows = IndexingRangeHelper.ApplyLookback(rows, lookbackBars);

        return await BuildAllForTimeframeAsync(
            symbol, timeframe, effectiveRows,
            new[] { featureType }, new[] { windowSize }, cancellationToken);
    }

    /// <summary>
    /// Giữ lại để tương thích với caller cũ.
    /// </summary>
    public async Task<int> UpsertIncrementalAsync(
        string symbol,
        string timeframe,
        string featureType,
        IReadOnlyList<KlineDto> rows,
        int windowSize,
        CancellationToken cancellationToken = default)
    {
        return await BuildAllForTimeframeAsync(
            symbol, timeframe, rows,
            new[] { featureType }, new[] { windowSize }, cancellationToken);
    }

    public async Task<(int Count, DateTime? LastUpdatedUtc)> GetStatusAsync(
        string symbol,
        string timeframe,
        string featureType,
        int windowSize,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = PatternVectorFeatureType.Normalize(featureType);
        var q = _db.WindowVectors.Where(x =>
            x.Symbol == symbol &&
            x.Timeframe == timeframe &&
            x.FeatureType == normalizedType &&
            x.WindowSize == windowSize &&
            x.Version == VectorVersion);
        var count = await q.CountAsync(cancellationToken);
        var latest = await q.OrderByDescending(x => x.UpdatedAtUtc).Select(x => (DateTime?)x.UpdatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        return (count, latest);
    }

    private async Task<Dictionary<(string FeatureType, int WindowSize), HashSet<long>>> LoadExistingKeysAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<string> featureTypes,
        IReadOnlyList<int> windowSizes,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, int), HashSet<long>>();
        foreach (var ft in featureTypes)
            foreach (var ws in windowSizes)
                result[(ft, ws)] = new HashSet<long>();

        var keys = await _db.WindowVectors
            .AsNoTracking()
            .Where(x =>
                x.Symbol == symbol &&
                x.Timeframe == timeframe &&
                featureTypes.Contains(x.FeatureType) &&
                windowSizes.Contains(x.WindowSize) &&
                x.Version == VectorVersion &&
                x.StartTimeMs >= startMs &&
                x.StartTimeMs <= endMs)
            .Select(x => new { x.FeatureType, x.WindowSize, x.StartTimeMs })
            .ToListAsync(cancellationToken);

        foreach (var key in keys)
        {
            var dictKey = (key.FeatureType, key.WindowSize);
            if (result.TryGetValue(dictKey, out var set))
                set.Add(key.StartTimeMs);
        }

        return result;
    }

    private async Task DeleteStaleVectorsAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<string> featureTypes,
        IReadOnlyList<int> windowSizes,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        var stale = await _db.WindowVectors
            .Where(x =>
                x.Symbol == symbol &&
                x.Timeframe == timeframe &&
                featureTypes.Contains(x.FeatureType) &&
                windowSizes.Contains(x.WindowSize) &&
                x.Version != VectorVersion &&
                x.StartTimeMs >= startMs &&
                x.StartTimeMs <= endMs)
            .ToListAsync(cancellationToken);

        if (stale.Count > 0)
        {
            _db.WindowVectors.RemoveRange(stale);
            await _db.SaveChangesAsync(cancellationToken);
            _db.ChangeTracker.Clear();
        }
    }

    public static float[]? BuildVector(IReadOnlyList<KlineDto> window, string featureType)
        => BuildVector(window, 0, window.Count, featureType);

    public static float[]? BuildVector(IReadOnlyList<KlineDto> source, int start, int count, string featureType)
    {
        if (count == 0) return null;
        if (string.Equals(featureType, PatternVectorFeatureType.ReturnsShape, StringComparison.OrdinalIgnoreCase))
        {
            return BuildReturnsShapeVector(source, start, count);
        }
        if (string.Equals(featureType, PatternVectorFeatureType.ReturnsLog, StringComparison.OrdinalIgnoreCase))
        {
            return BuildReturnsLogVector(source, start, count);
        }
        if (string.Equals(featureType, PatternVectorFeatureType.VolumeNorm, StringComparison.OrdinalIgnoreCase))
        {
            return BuildVolumeNormVector(source, start, count);
        }
        if (string.Equals(featureType, PatternVectorFeatureType.Volatility, StringComparison.OrdinalIgnoreCase))
        {
            return BuildVolatilityVector(source, start, count);
        }
        if (string.Equals(featureType, PatternVectorFeatureType.Trend, StringComparison.OrdinalIgnoreCase))
        {
            return BuildTrendVector(source, start, count);
        }

        double minLow = double.MaxValue, maxHigh = double.MinValue;
        for (int i = start; i < start + count; i++)
        {
            var c = source[i];
            if ((double)c.Low < minLow) minLow = (double)c.Low;
            if ((double)c.High > maxHigh) maxHigh = (double)c.High;
        }
        var range = maxHigh - minLow;
        if (range <= Epsilon) return null;

        return featureType switch
        {
            "open" => BuildNormalizedArray(source, start, count, c => (double)c.Open, minLow, range),
            "high" => BuildNormalizedArray(source, start, count, c => (double)c.High, minLow, range),
            "low" => BuildNormalizedArray(source, start, count, c => (double)c.Low, minLow, range),
            "close" => BuildNormalizedArray(source, start, count, c => (double)c.Close, minLow, range),
            _ => BuildAllVector(source, start, count, minLow, range)
        };
    }

    private static float[] BuildNormalizedArray(
        IReadOnlyList<KlineDto> source, int start, int count, Func<KlineDto, double> selector,
        double minLow, double range)
    {
        var vec = new float[count];
        for (int i = 0; i < count; i++)
            vec[i] = (float)((selector(source[start + i]) - minLow) / range);
        return vec;
    }

    private static float[] BuildAllVector(IReadOnlyList<KlineDto> source, int start, int count, double minLow, double range)
    {
        var vec = new float[count * 4];
        var offset = 0;
        for (int i = 0; i < count; i++)
        {
            var c = source[start + i];
            vec[offset++] = (float)(((double)c.Open - minLow) / range);
            vec[offset++] = (float)(((double)c.Close - minLow) / range);
            vec[offset++] = (float)(((double)c.High - minLow) / range);
            vec[offset++] = (float)(((double)c.Low - minLow) / range);
        }
        return vec;
    }

    private static float[] BuildReturnsShapeVector(IReadOnlyList<KlineDto> source, int start, int count)
    {
        var vec = new float[count * 4];
        var offset = 0;
        var previousClose = (double)source[start].Close;

        for (var i = 0; i < count; i++)
        {
            var c = source[start + i];
            var open = (double)c.Open;
            var close = (double)c.Close;
            var high = (double)c.High;
            var low = (double)c.Low;

            var ret = i == 0 || Math.Abs(previousClose) <= Epsilon
                ? 0.0
                : (close / previousClose) - 1.0;

            var candleRange = high - low;
            var body = 0.0;
            var upperWick = 0.0;
            var lowerWick = 0.0;

            if (candleRange > Epsilon)
            {
                body = Math.Abs(close - open) / candleRange;
                upperWick = (high - Math.Max(open, close)) / candleRange;
                lowerWick = (Math.Min(open, close) - low) / candleRange;
            }

            vec[offset++] = (float)ret;
            vec[offset++] = (float)body;
            vec[offset++] = (float)upperWick;
            vec[offset++] = (float)lowerWick;

            previousClose = close;
        }

        return vec;
    }

    private static float[] BuildReturnsLogVector(IReadOnlyList<KlineDto> source, int start, int count)
    {
        var raw = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (i == 0 || (double)source[start + i - 1].Close <= Epsilon)
                raw[i] = 0.0;
            else
                raw[i] = Math.Log((double)source[start + i].Close / (double)source[start + i - 1].Close);
        }
        return MinMaxNormalize(raw);
    }

    private static float[] BuildVolumeNormVector(IReadOnlyList<KlineDto> source, int start, int count)
    {
        var volumes = new double[count];
        for (int i = 0; i < count; i++)
            volumes[i] = (double)source[start + i].Volume;

        var avg = volumes.Average();
        if (avg <= Epsilon) return volumes.Select(_ => 0.5f).ToArray();
        var raw = volumes.Select(v => v / avg).ToArray();
        return MinMaxNormalize(raw);
    }

    private static float[] BuildVolatilityVector(IReadOnlyList<KlineDto> source, int start, int count)
    {
        var returns = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (i == 0 || (double)source[start + i - 1].Close <= Epsilon)
                returns[i] = 0.0;
            else
                returns[i] = Math.Log((double)source[start + i].Close / (double)source[start + i - 1].Close);
        }

        var vol = new double[count];
        for (int i = 0; i < count; i++)
        {
            int windowStart = Math.Max(0, i - 4); // rolling window 5 bars
            double sum = 0, sumSq = 0;
            int n = 0;
            for (int j = windowStart; j <= i; j++)
            {
                sum += returns[j];
                sumSq += returns[j] * returns[j];
                n++;
            }
            var avg = sum / n;
            vol[i] = Math.Sqrt(sumSq / n - avg * avg);
        }
        return MinMaxNormalize(vol);
    }

    private static float[] BuildTrendVector(IReadOnlyList<KlineDto> source, int start, int count)
    {
        var n = count;
        var closes = new double[n];
        for (int i = 0; i < n; i++)
            closes[i] = (double)source[start + i].Close;

        var xMean = (n - 1) / 2.0;
        var yMean = closes.Average();
        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            var dx = i - xMean;
            num += dx * (closes[i] - yMean);
            den += dx * dx;
        }
        var slope = den == 0 ? 0 : num / den;
        var intercept = yMean - slope * xMean;
        var residuals = new double[n];
        for (int i = 0; i < n; i++)
            residuals[i] = closes[i] - (slope * i + intercept);
        return MinMaxNormalize(residuals);
    }

    private static float[] MinMaxNormalize(double[] values)
    {
        var min = values.Min();
        var max = values.Max();
        var range = max - min;
        if (range <= Epsilon)
            return values.Select(_ => 0.5f).ToArray();
        return values.Select(v => (float)((v - min) / range)).ToArray();
    }
}
