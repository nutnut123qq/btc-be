using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class WindowVectorIndexer : IWindowVectorIndexer
{
    public const int VectorVersion = 2;
    private const double Epsilon = 0.0000001;

    private readonly AppDbContext _db;
    private readonly IBinanceKlinesService _klines;

    public WindowVectorIndexer(AppDbContext db, IBinanceKlinesService klines)
    {
        _db = db;
        _klines = klines;
    }

    public async Task<int> BuildFullAsync(
        string symbol,
        string timeframe,
        string featureType,
        int lookbackBars,
        int windowSize,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = PatternVectorFeatureType.Normalize(featureType);
        if (string.IsNullOrEmpty(normalizedType)) return 0;

        // Xóa vector cũ có version khác để tránh stale data khi version bump
        var stale = await _db.WindowVectors
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.FeatureType == normalizedType && x.WindowSize == windowSize && x.Version != VectorVersion)
            .ToListAsync(cancellationToken);
        if (stale.Count > 0)
        {
            _db.WindowVectors.RemoveRange(stale);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var rows = await _klines.GetKlinesAsync(
            symbol: symbol,
            interval: timeframe,
            limit: Math.Clamp(lookbackBars, 100, 100_000),
            cancellationToken: cancellationToken);
        return await UpsertIncrementalAsync(symbol, timeframe, featureType, rows, windowSize, cancellationToken);
    }

    public async Task<int> UpsertIncrementalAsync(
        string symbol,
        string timeframe,
        string featureType,
        IReadOnlyList<KlineDto> rows,
        int windowSize,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count < windowSize) return 0;
        var normalizedType = PatternVectorFeatureType.Normalize(featureType);
        if (string.IsNullOrEmpty(normalizedType)) return 0;
        var upserted = 0;

        for (var start = 0; start <= rows.Count - windowSize; start++)
        {
            var window = rows.Skip(start).Take(windowSize).ToList();
            var vector = BuildVector(window, normalizedType);
            if (vector is null) continue;
            var norm = (float)Math.Sqrt(vector.Sum(x => x * x));
            var startMs = window[0].OpenTimeMs;
            var endMs = window[^1].OpenTimeMs;
            var existing = await _db.WindowVectors.FirstOrDefaultAsync(x =>
                    x.Symbol == symbol &&
                    x.Timeframe == timeframe &&
                    x.FeatureType == normalizedType &&
                    x.WindowSize == windowSize &&
                    x.StartTimeMs == startMs,
                cancellationToken);
            if (existing == null)
            {
                _db.WindowVectors.Add(new WindowVector
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    FeatureType = normalizedType,
                    WindowSize = windowSize,
                    StartTimeMs = startMs,
                    EndTimeMs = endMs,
                    Vector = vector,
                    VectorDim = vector.Length,
                    VectorNorm = norm,
                    Version = VectorVersion,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                existing.EndTimeMs = endMs;
                existing.Vector = vector;
                existing.VectorDim = vector.Length;
                existing.VectorNorm = norm;
                existing.Version = VectorVersion;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }

            upserted++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return upserted;
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

    public static float[]? BuildVector(IReadOnlyList<KlineDto> window, string featureType)
    {
        if (window.Count == 0) return null;
        if (string.Equals(featureType, PatternVectorFeatureType.ReturnsShape, StringComparison.OrdinalIgnoreCase))
        {
            return BuildReturnsShapeVector(window);
        }
        if (string.Equals(featureType, PatternVectorFeatureType.ReturnsLog, StringComparison.OrdinalIgnoreCase))
        {
            return BuildReturnsLogVector(window);
        }
        if (string.Equals(featureType, PatternVectorFeatureType.VolumeNorm, StringComparison.OrdinalIgnoreCase))
        {
            return BuildVolumeNormVector(window);
        }
        if (string.Equals(featureType, PatternVectorFeatureType.Volatility, StringComparison.OrdinalIgnoreCase))
        {
            return BuildVolatilityVector(window);
        }
        if (string.Equals(featureType, PatternVectorFeatureType.Trend, StringComparison.OrdinalIgnoreCase))
        {
            return BuildTrendVector(window);
        }

        var minLow = window.Min(x => (double)x.Low);
        var maxHigh = window.Max(x => (double)x.High);
        var range = maxHigh - minLow;
        if (range <= Epsilon) return null;

        return featureType switch
        {
            "open" => window.Select(c => (float)(((double)c.Open - minLow) / range)).ToArray(),
            "high" => window.Select(c => (float)(((double)c.High - minLow) / range)).ToArray(),
            "low" => window.Select(c => (float)(((double)c.Low - minLow) / range)).ToArray(),
            "close" => window.Select(c => (float)(((double)c.Close - minLow) / range)).ToArray(),
            _ => BuildAllVector(window, minLow, range)
        };
    }

    private static float[] BuildAllVector(IReadOnlyList<KlineDto> window, double minLow, double range)
    {
        var vec = new float[window.Count * 4];
        var offset = 0;
        foreach (var c in window)
        {
            vec[offset++] = (float)(((double)c.Open - minLow) / range);
            vec[offset++] = (float)(((double)c.Close - minLow) / range);
            vec[offset++] = (float)(((double)c.High - minLow) / range);
            vec[offset++] = (float)(((double)c.Low - minLow) / range);
        }
        return vec;
    }

    private static float[] BuildReturnsShapeVector(IReadOnlyList<KlineDto> window)
    {
        var size = window.Count;
        var vec = new float[size * 4];
        var offset = 0;
        var previousClose = (double)window[0].Close;

        for (var i = 0; i < size; i++)
        {
            var c = window[i];
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

    private static float[] BuildReturnsLogVector(IReadOnlyList<KlineDto> window)
    {
        var raw = new double[window.Count];
        for (int i = 0; i < window.Count; i++)
        {
            if (i == 0 || (double)window[i - 1].Close <= Epsilon)
                raw[i] = 0.0;
            else
                raw[i] = Math.Log((double)window[i].Close / (double)window[i - 1].Close);
        }
        return MinMaxNormalize(raw);
    }

    private static float[] BuildVolumeNormVector(IReadOnlyList<KlineDto> window)
    {
        var volumes = window.Select(c => (double)c.Volume).ToArray();
        var avg = volumes.Average();
        if (avg <= Epsilon) return volumes.Select(_ => 0.5f).ToArray();
        var raw = volumes.Select(v => v / avg).ToArray();
        return MinMaxNormalize(raw);
    }

    private static float[] BuildVolatilityVector(IReadOnlyList<KlineDto> window)
    {
        var returns = new double[window.Count];
        for (int i = 0; i < window.Count; i++)
        {
            if (i == 0 || (double)window[i - 1].Close <= Epsilon)
                returns[i] = 0.0;
            else
                returns[i] = Math.Log((double)window[i].Close / (double)window[i - 1].Close);
        }
        var vol = new double[window.Count];
        for (int i = 0; i < window.Count; i++)
        {
            int start = Math.Max(0, i - 4); // rolling window 5 bars
            var slice = returns.Skip(start).Take(i - start + 1).ToArray();
            var avg = slice.Average();
            var variance = slice.Average(r => (r - avg) * (r - avg));
            vol[i] = Math.Sqrt(variance);
        }
        return MinMaxNormalize(vol);
    }

    private static float[] BuildTrendVector(IReadOnlyList<KlineDto> window)
    {
        var n = window.Count;
        var closes = window.Select(c => (double)c.Close).ToArray();
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
